import { BridgeRuntime } from "./bridge/runtime.mjs";
import { logger } from "./logger.mjs";
import { parseArgs } from "./shared.mjs";
import { startBridgeServer } from "./server/bridge-server.mjs";
import path from "node:path";
import { rmSync, writeFileSync } from "node:fs";

const activeResources = [];

/**
 * 控制台靠这个 pid 文件停止"上一次启动但仍在运行"的桥接（keepBridgeOnExit）。
 * 没有 --log-path 时跳过，保证纯命令行用法不受影响。
 */
function writePidFile(options, port) {
  if (!options.logPath) {
    return "";
  }
  try {
    const pidFile = path.join(path.dirname(options.logPath), `bridge-${port}.pid`);
    writeFileSync(pidFile, String(process.pid), "utf8");
    return pidFile;
  } catch (error) {
    logger.warn("bridge.pid_file.error", "Failed to write pid file", { error });
    return "";
  }
}

function removePidFile(pidFile) {
  if (!pidFile) {
    return;
  }
  try {
    rmSync(pidFile, { force: true });
  } catch {
    // pid 文件残留不影响下次启动：控制台会校验进程是否真的在跑。
  }
}

function hasStatusCodeSymbol(error, code) {
  if (!error || typeof error !== "object") {
    return false;
  }

  return Object.getOwnPropertySymbols(error).some((symbol) => {
    return String(symbol) === "Symbol(status-code)" && error[symbol] === code;
  });
}

function isRecoverableWebSocketPayloadError(error) {
  if (!error || typeof error !== "object") {
    return false;
  }

  const message = error instanceof Error ? error.message : String(error);
  return (
    error.code === "WS_ERR_UNSUPPORTED_MESSAGE_LENGTH" ||
    hasStatusCodeSymbol(error, 1009) ||
    /Max payload size exceeded|Unsupported message length/i.test(message)
  );
}

function exitAfterFatalProcessError(event, error, origin) {
  logger.error(event, "Bridge process encountered an unrecoverable error", {
    error,
    origin,
  });
  process.exitCode = 1;
  setImmediate(() => process.exit(1));
}

function installProcessGuards() {
  process.on("uncaughtException", (error, origin) => {
    if (isRecoverableWebSocketPayloadError(error)) {
      logger.warn("process.websocket_payload_limit", "Ignored oversized WebSocket payload error", {
        error,
        origin,
      });
      return;
    }

    exitAfterFatalProcessError("process.uncaught_exception", error, origin);
  });

  process.on("unhandledRejection", (reason) => {
    if (isRecoverableWebSocketPayloadError(reason)) {
      logger.warn("process.websocket_payload_limit", "Ignored oversized WebSocket payload rejection", {
        error: reason,
      });
      return;
    }

    exitAfterFatalProcessError("process.unhandled_rejection", reason, "unhandledRejection");
  });
}

async function main() {
  installProcessGuards();
  const options = parseArgs(process.argv.slice(2));
  const runtime = new BridgeRuntime(options);
  const server = await startBridgeServer(runtime, options);
  activeResources.push(server);

  const actualPort = server.address()?.port || options.listenPort;
  const pidFile = writePidFile(options, actualPort);
  if (pidFile) {
    process.on("exit", () => removePidFile(pidFile));
    process.on("SIGINT", () => process.exit(0));
    process.on("SIGTERM", () => process.exit(0));
  }

  runtime.warmup().catch((error) => {
    logger.warn("bridge.warmup.error", "Bridge warmup failed", { error });
  });
}

main().catch((error) => {
  logger.error("bridge.fatal", "Bridge process failed", { error });
  process.exitCode = 1;
});
