import path from "node:path";
import { promises as fs } from "node:fs";
import { fileURLToPath } from "node:url";

const __dirname = path.dirname(fileURLToPath(import.meta.url));
// 控制台会把配置文件放到 %APPDATA%\WorkBuddyRemote\config.json（便携版放程序目录），
// 通过这个环境变量告诉桥接实际路径；没有设置时退回 v1 的项目根配置。
const CONFIG_FILE_PATH =
  process.env.WORKBUDDY_REMOTE_CONFIG || path.resolve(__dirname, "..", "workbuddy-remote.config.json");

const DEFAULT_CONFIG = Object.freeze({
  workbuddyExePath: "",
  workbuddyUserDataDir: "",
  runtimeRootDir: "",
  cdpPort: 9333,
  bridgePort: 8780,
  listenHost: "127.0.0.1",
  killWorkBuddyProcessesBeforeStart: false,
  hideWorkBuddyWindowAfterStart: false,
  showReadyWindow: false,
  workspaceRoots: [],
  maskBridgeModelSecrets: false,
  enableFileManager: true,
  enableRestart: true,
});

function normalizeString(value) {
  return typeof value === "string" ? value.trim() : "";
}

function normalizeStringArray(value) {
  if (!Array.isArray(value)) {
    return [];
  }

  return value.map((item) => normalizeString(item)).filter(Boolean);
}

function normalizeBoolean(value, fallback = false) {
  if (typeof value === "boolean") {
    return value;
  }

  if (typeof value !== "string") {
    return fallback;
  }

  const normalized = value.trim().toLowerCase();
  if (["1", "true", "yes", "on"].includes(normalized)) {
    return true;
  }

  if (["0", "false", "no", "off"].includes(normalized)) {
    return false;
  }

  return fallback;
}

function normalizePort(value, fallback) {
  if (typeof value === "number" && Number.isInteger(value) && value > 0) {
    return value;
  }

  if (typeof value === "string" && /^\d+$/.test(value.trim())) {
    const parsed = Number(value.trim());
    if (Number.isInteger(parsed) && parsed > 0) {
      return parsed;
    }
  }

  return fallback;
}

async function readConfigFile() {
  try {
    return await fs.readFile(CONFIG_FILE_PATH, "utf8");
  } catch (error) {
    if (error && typeof error === "object" && "code" in error && error.code === "ENOENT") {
      return "{}";
    }
    throw error;
  }
}

async function loadConfig() {
  const raw = JSON.parse(await readConfigFile());
  return {
    workbuddyExePath: normalizeString(raw?.workbuddyExePath),
    workbuddyUserDataDir: normalizeString(raw?.workbuddyUserDataDir),
    runtimeRootDir: normalizeString(raw?.runtimeRootDir),
    cdpPort: normalizePort(raw?.cdpPort, DEFAULT_CONFIG.cdpPort),
    bridgePort: normalizePort(raw?.bridgePort, DEFAULT_CONFIG.bridgePort),
    listenHost: normalizeString(raw?.listenHost) || DEFAULT_CONFIG.listenHost,
    killWorkBuddyProcessesBeforeStart: normalizeBoolean(
      raw?.killWorkBuddyProcessesBeforeStart,
      DEFAULT_CONFIG.killWorkBuddyProcessesBeforeStart
    ),
    hideWorkBuddyWindowAfterStart: normalizeBoolean(
      raw?.hideWorkBuddyWindowAfterStart,
      DEFAULT_CONFIG.hideWorkBuddyWindowAfterStart
    ),
    showReadyWindow: normalizeBoolean(
      raw?.showReadyWindow,
      DEFAULT_CONFIG.showReadyWindow
    ),
    workspaceRoots: normalizeStringArray(raw?.workspaceRoots),
    maskBridgeModelSecrets: normalizeBoolean(
      raw?.maskBridgeModelSecrets,
      DEFAULT_CONFIG.maskBridgeModelSecrets
    ),
    enableFileManager: normalizeBoolean(raw?.enableFileManager, DEFAULT_CONFIG.enableFileManager),
    enableRestart: normalizeBoolean(raw?.enableRestart, DEFAULT_CONFIG.enableRestart),
  };
}

async function loadBridgeUiConfig() {
  const config = await loadConfig();
  return {
    maskBridgeModelSecrets: config.maskBridgeModelSecrets,
    enableFileManager: config.enableFileManager,
    enableRestart: config.enableRestart,
  };
}

export { CONFIG_FILE_PATH, DEFAULT_CONFIG, loadConfig, loadBridgeUiConfig };
