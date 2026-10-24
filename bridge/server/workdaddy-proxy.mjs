import path from "node:path";
import { readFile } from "node:fs/promises";

import { logger } from "../logger.mjs";
import { buildWorkDaddyPanelSource, describeWorkDaddyInstall } from "./workdaddy-panel.mjs";

/**
 * WorkDaddy（https://github.com/babygoton/WorkDaddy）在 127.0.0.1 上跑一个本地 HTTP 服务，
 * 默认端口按 profile 依次是 47832 / 47833 / 47834 / 47835，请求要带 `x-workdaddy-token`。
 *
 * 这里做一层同源代理，让网页端（可能来自局域网/端口映射）也能调用它的账号接口，
 * 而不需要把 WorkDaddy 的端口暴露出去、也不需要浏览器能访问 127.0.0.1。
 */

const DEFAULT_PORTS = [47832, 47833, 47834, 47835];
const PROBE_TIMEOUT_MS = 1200;
const REQUEST_TIMEOUT_MS = 120_000;
const REVALIDATE = "public, max-age=0, must-revalidate";

let cachedBase = null;
let cachedAt = 0;
const CACHE_TTL_MS = 15_000;

function candidateDataDirs() {
  const appData = process.env.APPDATA || "";
  if (!appData) return [];
  return [
    path.join(appData, "WorkDaddy"),
    path.join(appData, "WorkDaddy AI"),
    path.join(appData, "CodeDaddy CN"),
    path.join(appData, "CodeDaddy"),
  ];
}

async function readApiToken() {
  for (const dir of candidateDataDirs()) {
    try {
      const token = (await readFile(path.join(dir, ".api-token"), "utf8")).trim();
      if (token) return token;
    } catch {
      // 继续找下一个 profile
    }
  }
  return "";
}

async function probe(port, token) {
  const controller = new AbortController();
  const timer = setTimeout(() => controller.abort(), PROBE_TIMEOUT_MS);
  try {
    const response = await fetch(`http://127.0.0.1:${port}/api/status`, {
      headers: { "x-workdaddy-token": token },
      signal: controller.signal,
    });
    return response.ok;
  } catch {
    return false;
  } finally {
    clearTimeout(timer);
  }
}

async function resolveBase() {
  const now = Date.now();
  if (cachedBase && now - cachedAt < CACHE_TTL_MS) {
    return cachedBase;
  }

  const overridePort = Number(process.env.WORKDADDY_API_PORT || 0);
  const ports = overridePort > 0 ? [overridePort, ...DEFAULT_PORTS] : DEFAULT_PORTS;
  const token = await readApiToken();
  if (!token) {
    cachedBase = null;
    cachedAt = now;
    return null;
  }

  for (const port of ports) {
    if (await probe(port, token)) {
      cachedBase = { port, token };
      cachedAt = now;
      return cachedBase;
    }
  }

  cachedBase = null;
  cachedAt = now;
  return null;
}

export async function getWorkDaddyStatus() {
  const base = await resolveBase();
  if (!base) {
    return { available: false };
  }
  return { available: true, port: base.port };
}

async function forward(req, res, base, pathname, search) {
  const target = `http://127.0.0.1:${base.port}${pathname}${search || ""}`;
  const headers = {
    "x-workdaddy-token": base.token,
    accept: "application/json",
  };
  const contentType = req.headers["content-type"];
  if (contentType) headers["content-type"] = contentType;

  const init = { method: req.method, headers, signal: AbortSignal.timeout(REQUEST_TIMEOUT_MS) };
  if (req.method !== "GET" && req.method !== "HEAD") {
    const chunks = [];
    for await (const chunk of req) chunks.push(chunk);
    init.body = Buffer.concat(chunks);
  }

  const upstream = await fetch(target, init);
  const body = Buffer.from(await upstream.arrayBuffer());
  res.writeHead(upstream.status, {
    "Content-Type": upstream.headers.get("content-type") || "application/json; charset=utf-8",
    "Cache-Control": "no-store",
    "Content-Length": body.byteLength,
  });
  res.end(body);
}

/**
 * 处理 /workdaddy/* 请求。返回 true 表示已处理。
 */
export async function handleWorkDaddyRequest(req, res, requestUrl) {
  const pathname = requestUrl.pathname;
  if (!pathname.startsWith("/workdaddy/")) {
    return false;
  }

  if (pathname === "/workdaddy/status") {
    const status = await getWorkDaddyStatus();
    const install = describeWorkDaddyInstall();
    res.writeHead(200, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
    res.end(JSON.stringify({ ...status, installed: install.installed }));
    return true;
  }

  if (pathname === "/workdaddy/panel.js") {
    const panel = await buildWorkDaddyPanelSource({ apiBase: "/workdaddy" });
    if (!panel) {
      res.writeHead(404, { "Content-Type": "text/plain; charset=utf-8", "Cache-Control": "no-store" });
      res.end("WorkDaddy panel is not available");
      return true;
    }
    if (req.headers["if-none-match"] === panel.etag) {
      res.writeHead(304, { ETag: panel.etag, "Cache-Control": REVALIDATE });
      res.end();
      return true;
    }
    res.writeHead(200, {
      "Content-Type": "application/javascript; charset=utf-8",
      "Cache-Control": REVALIDATE,
      ETag: panel.etag,
      "Content-Length": Buffer.byteLength(panel.source),
    });
    res.end(panel.source);
    return true;
  }

  const base = await resolveBase();
  if (!base) {
    res.writeHead(503, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
    res.end(JSON.stringify({ ok: false, error: "WorkDaddy 本地服务未运行或未安装" }));
    return true;
  }

  const upstreamPath = pathname.slice("/workdaddy".length) || "/api/status";
  if (!upstreamPath.startsWith("/api/")) {
    res.writeHead(404, { "Content-Type": "application/json; charset=utf-8" });
    res.end(JSON.stringify({ ok: false, error: "unsupported workdaddy path" }));
    return true;
  }

  try {
    await forward(req, res, base, upstreamPath, requestUrl.search);
  } catch (error) {
    logger.warn("workdaddy.proxy.error", "WorkDaddy proxy request failed", {
      path: upstreamPath,
      error,
    });
    if (!res.headersSent) {
      res.writeHead(502, { "Content-Type": "application/json; charset=utf-8", "Cache-Control": "no-store" });
      res.end(JSON.stringify({ ok: false, error: error instanceof Error ? error.message : String(error) }));
    } else {
      res.end();
    }
  }
  return true;
}
