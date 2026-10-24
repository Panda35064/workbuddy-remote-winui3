import http from "node:http";
import https from "node:https";
import os from "node:os";
import path from "node:path";
import { createReadStream } from "node:fs";
import { readFile, readdir, stat } from "node:fs/promises";
import { pipeline } from "node:stream/promises";
import zlib from "node:zlib";

import { AsarArchive } from "../asar.mjs";
import {
  EXTENSION_HTTP_BASE,
  WebSocketServer,
  contentTypeFor,
  getLanUrls,
  json,
  NO_STORE_CACHE_CONTROL,
  readJsonBody,
  resolveWorkBuddyAsarPath,
  rewriteExtensionSchemeUrls,
  text,
} from "../shared.mjs";
import { logger, summarizeMessage } from "../logger.mjs";
import {
  renderWorkBuddyNativeHtml,
  renderWorkBuddyNativeShimJs,
} from "../web/workbuddy-native.mjs";
import { createBridgeAccessAuth } from "./access-auth.mjs";
import { handleWorkDaddyRequest } from "./workdaddy-proxy.mjs";
import { listDirectory, listRoots, makeDirectory } from "./fs-browse.mjs";
import { loadBridgeUiConfig, loadConfig } from "../config.mjs";
import {
  createWorkspaceFolder,
  deleteWorkspaceFolder,
  deleteWorkspaceEntry,
  listAvailableWorkspaceRoots,
  listWorkspaceEntries,
  listWorkspaceFolders,
  renameWorkspaceFolder,
  resolveWorkspaceFilePath,
  uploadWorkspaceFile,
} from "../workspace/service.mjs";

const HTML_CACHE_CONTROL = "no-cache";
const IMMUTABLE_CACHE_CONTROL = "public, max-age=31536000, immutable";
const REVALIDATED_STATIC_CACHE_CONTROL = "public, max-age=0, must-revalidate";
const FAVICON_CACHE_CONTROL = "public, max-age=86400";
const FAVICON_PATHS = new Set([
  "/favicon.ico",
  "/favicon.png",
  "/apple-touch-icon.png",
  "/apple-touch-icon-precomposed.png",
]);
const FAVICON_LINK_TAGS = [
  '<link rel="icon" href="/favicon.ico" sizes="any">',
  '<link rel="icon" type="image/png" href="/favicon.png">',
  '<link rel="apple-touch-icon" href="/apple-touch-icon.png">',
].join("");
const COMPRESSIBLE_EXTENSIONS = new Set([".html", ".js", ".mjs", ".css", ".json", ".svg"]);
const MAX_COMPRESSED_STATIC_ASSET_BYTES = 32 * 1024 * 1024;
const BROWSER_WS_MAX_PAYLOAD_BYTES = readPositiveIntegerEnv(
  ["WORKBUDDY_REMOTE_BROWSER_WS_MAX_PAYLOAD_BYTES", "WORKBUDDY_REMOTE_MAX_PAYLOAD_BYTES"],
  512 * 1024 * 1024
);
const versionedScriptCompressionCache = new Map();
const WORKBUDDY_ASAR_PATH = resolveWorkBuddyAsarPath();
const workBuddyAsar = new AsarArchive(WORKBUDDY_ASAR_PATH);
// Electron 用 wb-extension://<id>/<path> 从这些目录读扩展资源（内置扩展在
// app.asar.unpacked 下，安装版扩展在用户数据目录）。浏览器没有该协议，
// 由 bridge 用 /wb-extension/<id>/<path> 提供同样的内容。
const WORKBUDDY_RESOURCES_DIR = path.dirname(WORKBUDDY_ASAR_PATH);
const EXTENSION_RESOURCE_ROOTS = [
  path.join(WORKBUDDY_RESOURCES_DIR, "app.asar.unpacked", "resources", "extensions"),
  path.join(WORKBUDDY_RESOURCES_DIR, "extensions"),
  path.join(process.env.APPDATA || "", "WorkBuddy", "extensions"),
  path.join(process.env.LOCALAPPDATA || "", "WorkBuddy", "extensions"),
].filter((root) => root && !root.startsWith("WorkBuddy"));
const EXTENSION_TEXT_ASSET_PATTERN = /\.(?:css|html|js|json|map|mjs|svg)$/iu;
// shim 首屏要读 WorkBuddy 的 buddyAPI 方法表/版本/语言，这些都要经过 CDP。
// CDP 偶发卡住时不能让整页永远打不开（浏览器会一直等 shim 这个阻塞脚本），
// 所以给每次读取加硬超时，并用上次成功的结果兜底。
const SHIM_METADATA_TIMEOUT_MS = readPositiveIntegerEnv(
  ["WORKBUDDY_REMOTE_SHIM_METADATA_TIMEOUT_MS"],
  5000
);
const SHIM_METADATA_UNAVAILABLE = Symbol("shim-metadata-unavailable");
const shimMetadataCache = new Map();
const MODEL_SECRET_READ_METHODS = new Set([
  "configGet",
  "configGetAll",
  "configGetLocalCustomModels",
  "configSaveLocalCustomModel",
  "configDeleteLocalCustomModel",
]);
const MODEL_SECRET_WRITE_METHODS = new Set([
  "configSet",
  "configSaveLocalCustomModel",
]);
const REDACTED_MODEL_API_KEY = "workbuddy-remote-redacted-api-key";
const REDACTED_MODEL_ENDPOINT = "https://workbuddy-remote.local/redacted/chat/completions";
const LOOPBACK_PROXY_PREFIX = "/bridge/loopback/";
const LOOPBACK_PROXY_MAX_REWRITE_BYTES = 32 * 1024 * 1024;
const HOP_BY_HOP_HEADERS = new Set([
  "connection",
  "keep-alive",
  "proxy-authenticate",
  "proxy-authorization",
  "te",
  "trailer",
  "transfer-encoding",
  "upgrade",
]);
const TEXT_PROXY_CONTENT_TYPE_PATTERN =
  /^(?:text\/|application\/(?:javascript|json|xml|xhtml\+xml)|image\/svg\+xml)\b/iu;
const LOOPBACK_ORIGIN_PATTERN =
  /\bhttps?:\/\/(?:127\.0\.0\.1|localhost|\[::1\])(?::\d{1,5})?/giu;
const ESCAPED_LOOPBACK_ORIGIN_PATTERN =
  /\bhttps?:\\\/\\\/(?:127\.0\.0\.1|localhost|\[::1\])(?::\d{1,5})?/giu;
const ENCODED_LOOPBACK_ORIGIN_PATTERN =
  /\bhttps?%3A%2F%2F(?:localhost|127(?:\.|%2E)0(?:\.|%2E)0(?:\.|%2E)1|%5B%3A%3A1%5D)(?:%3A\d{1,5})?/giu;
const ROOT_RELATIVE_PROXY_PATH_PATTERN =
  /\b((?:src|href|action|data-[\w-]+)\s*=\s*["'])\/(?!\/|bridge\/loopback\/)(?=(?:static|doc|sheet|slide|form|desktop|tools|tdoc)\b)|(\burl\(\s*["']?)\/(?!\/)|(["'`])\/(?!\/)(?=(?:static|doc|sheet|slide|form|desktop|tools|tdoc)\b)/giu;
const ESCAPED_ROOT_RELATIVE_PROXY_PATH_PATTERN =
  /(["'`])\\\/(?!\\\/)(?=(?:static|doc|sheet|slide|form|desktop|tools|tdoc)\b)/giu;

function readPositiveIntegerEnv(names, fallback) {
  const candidates = Array.isArray(names) ? names : [names];
  for (const name of candidates) {
    const raw = process.env[name];
    if (!raw) {
      continue;
    }
    const parsed = Number(raw);
    if (Number.isFinite(parsed) && parsed > 0) {
      return Math.trunc(parsed);
    }
  }
  return fallback;
}

async function loadFeatureFlags() {
  const config = await loadConfig();
  return {
    enableFileManager: config.enableFileManager !== false,
    enableRestart: config.enableRestart !== false,
  };
}

async function isFileManagerEnabled() {
  return (await loadFeatureFlags()).enableFileManager;
}

async function isRestartEnabled() {
  return (await loadFeatureFlags()).enableRestart;
}

function normalizeSecretFieldName(name) {
  return String(name || "").replace(/[\s_-]/g, "").toLowerCase();
}

function getModelSecretFieldType(name) {
  const normalized = normalizeSecretFieldName(name);
  if (
    normalized === "apikey" ||
    normalized === "secretkey" ||
    normalized.endsWith(".apikey") ||
    normalized.endsWith(".secretkey")
  ) {
    return "apiKey";
  }
  if (
    normalized === "endpoint" ||
    normalized === "baseurl" ||
    normalized === "apiurl" ||
    normalized.endsWith(".endpoint") ||
    normalized.endsWith(".baseurl") ||
    normalized.endsWith(".apiurl")
  ) {
    return "endpoint";
  }
  return "";
}

function isRedactedModelSecretValue(value) {
  return value === REDACTED_MODEL_API_KEY || value === REDACTED_MODEL_ENDPOINT;
}

function getModelSecretPlaceholder(fieldType) {
  return fieldType === "endpoint" ? REDACTED_MODEL_ENDPOINT : REDACTED_MODEL_API_KEY;
}

function getModelSecretIdentity(value) {
  if (!value || typeof value !== "object" || Array.isArray(value)) {
    return "";
  }

  const parts = [];
  for (const key of ["id", "modelId", "providerId", "name"]) {
    const item = value[key];
    if (typeof item === "string" && item.trim()) {
      parts.push(`${key}:${item.trim()}`);
    }
  }
  return parts.length > 0 ? parts.join("|") : "";
}

function createModelSecretProtector() {
  const valuesByPath = new Map();
  const valuesByIdentity = new Map();

  function remember(pathKey, identity, fieldName, value) {
    valuesByPath.set(pathKey, value);
    if (!identity) {
      return;
    }
    const current = valuesByIdentity.get(identity) || {};
    current[fieldName] = value;
    valuesByIdentity.set(identity, current);
  }

  function findCached(pathKey, identity, fieldName) {
    if (identity) {
      const current = valuesByIdentity.get(identity);
      if (current && Object.prototype.hasOwnProperty.call(current, fieldName)) {
        return current[fieldName];
      }
    }
    if (valuesByPath.has(pathKey)) {
      return valuesByPath.get(pathKey);
    }
    return undefined;
  }

  function protect(value, path = []) {
    if (Array.isArray(value)) {
      return value.map((item, index) => protect(item, [...path, String(index)]));
    }

    if (!value || typeof value !== "object") {
      return value;
    }

    const identity = getModelSecretIdentity(value);
    const result = {};
    for (const [key, nestedValue] of Object.entries(value)) {
      const fieldType = getModelSecretFieldType(key);
      const pathKey = [...path, key].join(".");
      if (fieldType && typeof nestedValue === "string" && nestedValue) {
        remember(pathKey, identity, key, nestedValue);
        result[key] = getModelSecretPlaceholder(fieldType);
      } else {
        result[key] = protect(nestedValue, [...path, key]);
      }
    }
    return result;
  }

  function restore(value, path = []) {
    if (Array.isArray(value)) {
      return value.map((item, index) => restore(item, [...path, String(index)]));
    }

    if (!value || typeof value !== "object") {
      return value;
    }

    const identity = getModelSecretIdentity(value);
    const result = {};
    for (const [key, nestedValue] of Object.entries(value)) {
      const fieldType = getModelSecretFieldType(key);
      const pathKey = [...path, key].join(".");
      if (fieldType && isRedactedModelSecretValue(nestedValue)) {
        const cached = findCached(pathKey, identity, key);
        if (cached === undefined) {
          throw new Error("A redacted model secret could not be restored. Re-enter it before saving.");
        }
        result[key] = cached;
      } else {
        result[key] = restore(nestedValue, [...path, key]);
      }
    }
    return result;
  }

  function protectConfigGetResult(args, result) {
    const configKey = typeof args?.[0] === "string" ? args[0] : "";
    const fieldType = getModelSecretFieldType(configKey);
    if (fieldType && typeof result === "string" && result) {
      remember(`config.${configKey}`, "", configKey, result);
      return getModelSecretPlaceholder(fieldType);
    }
    return protect(result);
  }

  function restoreConfigSetArgs(args) {
    if (
      Array.isArray(args) &&
      typeof args[0] === "string" &&
      getModelSecretFieldType(args[0]) &&
      isRedactedModelSecretValue(args[1])
    ) {
      const cached = findCached(`config.${args[0]}`, "", args[0]);
      if (cached === undefined) {
        throw new Error("A redacted model secret could not be restored. Re-enter it before saving.");
      }
      return [args[0], cached, ...args.slice(2)];
    }
    return restore(args);
  }

  return {
    protectResult(method, args, result) {
      if (!MODEL_SECRET_READ_METHODS.has(method)) {
        return result;
      }
      return method === "configGet" ? protectConfigGetResult(args, result) : protect(result);
    },
    restoreArgs(method, args) {
      if (!MODEL_SECRET_WRITE_METHODS.has(method)) {
        return args;
      }
      return method === "configSet" ? restoreConfigSetArgs(args) : restore(args);
    },
  };
}

function writeFeatureDisabled(res, featureName) {
  json(res, 403, {
    ok: false,
    error: `${featureName} is disabled by config.`,
  });
}

function defaultPortForProtocol(protocol) {
  return protocol === "https:" || protocol === "https" ? "443" : "80";
}

function isLoopbackProxyHostname(hostname) {
  const value = String(hostname || "").trim().toLowerCase().replace(/^\[|\]$/g, "");
  return value === "127.0.0.1" || value === "localhost" || value === "::1";
}

function isAllowedLoopbackProxyUrl(url) {
  return (
    (url.protocol === "http:" || url.protocol === "https:") &&
    isLoopbackProxyHostname(url.hostname)
  );
}

function loopbackProxyOriginPathForUrl(value) {
  const url = value instanceof URL ? value : new URL(String(value));
  if (!isAllowedLoopbackProxyUrl(url)) {
    return "";
  }

  const protocol = url.protocol.replace(/:$/u, "");
  const port = url.port || defaultPortForProtocol(url.protocol);
  return `${LOOPBACK_PROXY_PREFIX}${protocol}/${port}`;
}

function rewriteLoopbackUrlOrigins(value) {
  if (typeof value !== "string" || !value) {
    return value;
  }

  return value
    .replace(LOOPBACK_ORIGIN_PATTERN, (match) => {
      try {
        return loopbackProxyOriginPathForUrl(match) || match;
      } catch {
        return match;
      }
    })
    .replace(ESCAPED_LOOPBACK_ORIGIN_PATTERN, (match) => {
      try {
        const rewritten = loopbackProxyOriginPathForUrl(match.replace(/\\\//gu, "/"));
        return rewritten ? rewritten.replace(/\//gu, "\\/") : match;
      } catch {
        return match;
      }
    })
    .replace(ENCODED_LOOPBACK_ORIGIN_PATTERN, (match) => {
      try {
        const rewritten = loopbackProxyOriginPathForUrl(decodeURIComponent(match));
        return rewritten ? encodeURIComponent(rewritten) : match;
      } catch {
        return match;
      }
    });
}

function escapedProxyOriginPath(proxyOriginPath) {
  return String(proxyOriginPath || "").replace(/\//gu, "\\/");
}

function rewriteRootRelativeProxyPaths(value, proxyOriginPath) {
  if (typeof value !== "string" || !value || !proxyOriginPath) {
    return value;
  }

  return value
    .replace(ROOT_RELATIVE_PROXY_PATH_PATTERN, (match, attrPrefix, cssPrefix, quotePrefix) => {
      const prefix = attrPrefix || cssPrefix || quotePrefix || "";
      return `${prefix}${proxyOriginPath}/`;
    })
    .replace(ESCAPED_ROOT_RELATIVE_PROXY_PATH_PATTERN, (match, quotePrefix) => {
      return `${quotePrefix}${escapedProxyOriginPath(proxyOriginPath)}\\/`;
    });
}

function rewriteProxyBodyUrls(value, proxyOriginPath) {
  return rewriteRootRelativeProxyPaths(rewriteLoopbackUrlOrigins(value), proxyOriginPath);
}

function parseLoopbackProxyTarget(requestUrl) {
  if (!requestUrl.pathname.startsWith(LOOPBACK_PROXY_PREFIX)) {
    return null;
  }

  const rest = requestUrl.pathname.slice(LOOPBACK_PROXY_PREFIX.length);
  const slashIndex = rest.indexOf("/");
  if (slashIndex <= 0) {
    return null;
  }

  const protocol = rest.slice(0, slashIndex);
  const portAndPath = rest.slice(slashIndex + 1);
  const nextSlashIndex = portAndPath.indexOf("/");
  const portText = nextSlashIndex >= 0 ? portAndPath.slice(0, nextSlashIndex) : portAndPath;
  const targetPath = nextSlashIndex >= 0 ? portAndPath.slice(nextSlashIndex) : "/";
  if (protocol !== "http" && protocol !== "https") {
    return null;
  }

  const port = Number(portText);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    return null;
  }

  return new URL(`${protocol}://127.0.0.1:${port}${targetPath || "/"}${requestUrl.search}`);
}

function parseLoopbackProxyTargetFromReferer(requestUrl, referer) {
  if (requestUrl.pathname.startsWith(LOOPBACK_PROXY_PREFIX)) {
    return null;
  }

  const rawReferer = typeof referer === "string" ? referer : "";
  if (!rawReferer) {
    return null;
  }

  let refererUrl;
  try {
    refererUrl = new URL(rawReferer);
  } catch {
    return null;
  }

  const rest = refererUrl.pathname.startsWith(LOOPBACK_PROXY_PREFIX)
    ? refererUrl.pathname.slice(LOOPBACK_PROXY_PREFIX.length)
    : "";
  const firstSlashIndex = rest.indexOf("/");
  if (firstSlashIndex <= 0) {
    return null;
  }

  const protocol = rest.slice(0, firstSlashIndex);
  const remainder = rest.slice(firstSlashIndex + 1);
  const nextSlashIndex = remainder.indexOf("/");
  const portText = nextSlashIndex >= 0 ? remainder.slice(0, nextSlashIndex) : remainder;
  if (protocol !== "http" && protocol !== "https") {
    return null;
  }

  const port = Number(portText);
  if (!Number.isInteger(port) || port < 1 || port > 65535) {
    return null;
  }

  return new URL(`${protocol}://127.0.0.1:${port}${requestUrl.pathname}${requestUrl.search}`);
}

function sanitizeProxyRequestHeaders(headers) {
  const result = {};
  for (const [name, value] of Object.entries(headers || {})) {
    const lowerName = name.toLowerCase();
    if (
      HOP_BY_HOP_HEADERS.has(lowerName) ||
      lowerName === "host" ||
      lowerName === "cookie" ||
      lowerName === "authorization"
    ) {
      continue;
    }

    result[name] = lowerName === "accept-encoding" ? "identity" : value;
  }

  result["accept-encoding"] = "identity";
  return result;
}

function rewriteLocationHeaderValue(value, proxyOriginPath) {
  if (Array.isArray(value)) {
    return value.map((entry) => rewriteLocationHeaderValue(entry, proxyOriginPath));
  }
  if (typeof value !== "string") {
    return value;
  }

  return rewriteRootRelativeProxyPaths(rewriteLoopbackUrlOrigins(value), proxyOriginPath);
}

function sanitizeProxyResponseHeaders(headers, { rewritingBody = false, proxyOriginPath = "" } = {}) {
  const result = {};
  for (const [name, value] of Object.entries(headers || {})) {
    const lowerName = name.toLowerCase();
    if (
      HOP_BY_HOP_HEADERS.has(lowerName) ||
      (rewritingBody && (lowerName === "content-length" || lowerName === "content-encoding"))
    ) {
      continue;
    }

    result[name] = lowerName === "location" ? rewriteLocationHeaderValue(value, proxyOriginPath) : value;
  }

  result["Cache-Control"] = "no-store";
  return result;
}

function getHeaderValue(headers, name) {
  const value = headers?.[name.toLowerCase()];
  return Array.isArray(value) ? value.join(", ") : String(value || "");
}

function shouldRewriteProxyBody(headers) {
  const contentEncoding = getHeaderValue(headers, "content-encoding");
  if (contentEncoding && contentEncoding.toLowerCase() !== "identity") {
    return false;
  }

  const contentType = getHeaderValue(headers, "content-type");
  if (/^text\/event-stream\b/iu.test(contentType)) {
    return false;
  }

  return TEXT_PROXY_CONTENT_TYPE_PATTERN.test(contentType);
}

async function collectProxyBody(stream) {
  const chunks = [];
  let totalBytes = 0;
  for await (const chunk of stream) {
    const buffer = Buffer.isBuffer(chunk) ? chunk : Buffer.from(chunk);
    totalBytes += buffer.byteLength;
    chunks.push(buffer);
  }

  return {
    body: Buffer.concat(chunks, totalBytes),
    canRewrite: totalBytes <= LOOPBACK_PROXY_MAX_REWRITE_BYTES,
  };
}

function proxyLoopbackRequest(req, res, targetUrl) {
  const client = targetUrl.protocol === "https:" ? https : http;
  const proxyOriginPath = loopbackProxyOriginPathForUrl(targetUrl);
  const requestOptions = {
    method: req.method,
    headers: sanitizeProxyRequestHeaders(req.headers),
    ...(targetUrl.protocol === "https:" ? { rejectUnauthorized: false } : {}),
  };

  return new Promise((resolve, reject) => {
    const proxyReq = client.request(targetUrl, requestOptions, async (proxyRes) => {
      try {
        if (/^text\/event-stream\b/iu.test(getHeaderValue(proxyRes.headers, "content-type"))) {
          proxyReq.setTimeout(0);
        }

        const rewritingBody = shouldRewriteProxyBody(proxyRes.headers);
        if (!rewritingBody) {
          res.writeHead(
            proxyRes.statusCode || 502,
            sanitizeProxyResponseHeaders(proxyRes.headers, { proxyOriginPath })
          );
          proxyRes.pipe(res);
          proxyRes.on("end", resolve);
          proxyRes.on("error", reject);
          res.on("error", reject);
          return;
        }

        const { body, canRewrite } = await collectProxyBody(proxyRes);
        const responseHeaders = sanitizeProxyResponseHeaders(proxyRes.headers, {
          rewritingBody: true,
          proxyOriginPath,
        });
        const responseBody = canRewrite
          ? Buffer.from(rewriteProxyBodyUrls(body.toString("utf8"), proxyOriginPath), "utf8")
          : body;
        res.writeHead(proxyRes.statusCode || 502, {
          ...responseHeaders,
          "Content-Length": responseBody.byteLength,
        });
        res.end(responseBody);
        resolve();
      } catch (error) {
        reject(error);
      }
    });

    proxyReq.setTimeout(30000, () => {
      proxyReq.destroy(new Error(`Loopback proxy timed out: ${targetUrl.href}`));
    });
    proxyReq.on("error", reject);
    req.on("error", reject);
    req.pipe(proxyReq);
  });
}

function isCompressibleAsset(filePath) {
  return COMPRESSIBLE_EXTENSIONS.has(path.extname(filePath).toLowerCase());
}

function pickContentEncoding(req) {
  const acceptEncoding = String(req.headers["accept-encoding"] || "");
  if (acceptEncoding.includes("br")) {
    return "br";
  }
  if (acceptEncoding.includes("gzip")) {
    return "gzip";
  }
  return null;
}

function createCompressor(encoding) {
  if (encoding === "br") {
    return zlib.createBrotliCompress({
      params: {
        [zlib.constants.BROTLI_PARAM_QUALITY]: 4,
      },
    });
  }

  return zlib.createGzip({ level: zlib.constants.Z_BEST_SPEED });
}

function compressBufferSync(body, encoding) {
  if (encoding === "br") {
    return zlib.brotliCompressSync(body, {
      params: {
        [zlib.constants.BROTLI_PARAM_QUALITY]: 4,
      },
    });
  }

  return zlib.gzipSync(body, { level: zlib.constants.Z_BEST_SPEED });
}

function isEtagMatch(req, etag) {
  const header = req.headers["if-none-match"];
  if (!header) {
    return false;
  }
  return String(header)
    .split(",")
    .map((value) => value.trim())
    .includes(etag);
}

function isModifiedSinceMatch(req, stats) {
  const header = req.headers["if-modified-since"];
  if (!header) {
    return false;
  }

  const parsed = Date.parse(String(header));
  if (Number.isNaN(parsed)) {
    return false;
  }

  return Math.trunc(stats.mtimeMs / 1000) <= Math.trunc(parsed / 1000);
}

function writeNotModified(res, headers = {}) {
  res.writeHead(304, headers);
  res.end();
}

function writeBuffer(res, statusCode, body, headers) {
  res.writeHead(statusCode, {
    ...headers,
    "Content-Length": body.byteLength,
  });
  res.end(body);
}

async function readShimMetadata(key, producer, fallback) {
  const cached = shimMetadataCache.get(key);
  let timer;
  const timeoutPromise = new Promise((resolve) => {
    timer = setTimeout(() => resolve(SHIM_METADATA_UNAVAILABLE), SHIM_METADATA_TIMEOUT_MS);
    if (typeof timer.unref === "function") {
      timer.unref();
    }
  });

  try {
    const value = await Promise.race([producer(), timeoutPromise]);
    if (value === SHIM_METADATA_UNAVAILABLE) {
      logger.warn("shim.metadata.timeout", "WorkBuddy metadata lookup timed out", {
        key,
        timeoutMs: SHIM_METADATA_TIMEOUT_MS,
        servedFromCache: cached !== undefined,
      });
      return cached === undefined ? fallback : cached;
    }

    const isEmpty =
      value === undefined ||
      value === null ||
      (typeof value === "string" && !value) ||
      (Array.isArray(value) && !value.length);
    if (isEmpty) {
      return cached === undefined ? fallback : cached;
    }

    shimMetadataCache.set(key, value);
    return value;
  } catch (error) {
    logger.warn("shim.metadata.error", "WorkBuddy metadata lookup failed", { key, error });
    return cached === undefined ? fallback : cached;
  } finally {
    clearTimeout(timer);
  }
}

// ---- wb-extension:// 兼容层 -------------------------------------------------
// 官方把内置扩展放在 resources/app.asar.unpacked/resources/extensions/<id>，
// 安装版扩展在用户数据目录。浏览器里没有这个自定义协议，这里按 HTTP 路径还原。
async function writeExtensionAssetBody(req, res, relativePath, buffer, stats) {
  let body = buffer;
  if (EXTENSION_TEXT_ASSET_PATTERN.test(relativePath)) {
    const source = buffer.toString("utf8");
    const rewritten = rewriteExtensionSchemeUrls(source);
    if (rewritten !== source) {
      body = Buffer.from(rewritten, "utf8");
    }
  }

  const headers = {
    "Content-Type": contentTypeFor(relativePath),
    "Cache-Control": NO_STORE_CACHE_CONTROL,
    "Access-Control-Allow-Origin": "*",
    "Cross-Origin-Resource-Policy": "cross-origin",
    "Content-Length": body.length,
    ETag: `"ext-${body.length}-${Math.trunc(stats?.mtimeMs ?? 0)}"`,
  };

  if (req.method === "HEAD") {
    res.writeHead(200, headers);
    res.end();
    return;
  }

  res.writeHead(200, headers);
  res.end(body);
}

async function sendExtensionAsset(req, res, requestUrl) {
  if (req.method === "OPTIONS") {
    res.writeHead(204, {
      "Access-Control-Allow-Origin": "*",
      "Access-Control-Allow-Methods": "GET, HEAD, OPTIONS",
      "Access-Control-Allow-Headers": "*",
    });
    res.end();
    return;
  }

  let decoded;
  try {
    decoded = decodeURIComponent(requestUrl.pathname.slice(EXTENSION_HTTP_BASE.length));
  } catch {
    json(res, 400, { error: "Invalid extension path" });
    return;
  }

  const separatorIndex = decoded.indexOf("/");
  if (separatorIndex <= 0) {
    json(res, 400, { error: "Missing extension id" });
    return;
  }

  const extensionId = decoded.slice(0, separatorIndex);
  const relativePath = decoded.slice(separatorIndex + 1).replace(/^\/+/u, "");
  let segments = relativePath.split("/").filter(Boolean);
  // 容错：某些调用方会用改写过一次的地址再拼一遍，出现
  // /wb-extension/<id>/wb-extension/<id>/... 这种重复段时剥掉。
  while (segments[0] === "wb-extension" && segments[1] === extensionId) {
    segments = segments.slice(2);
  }
  if (!/^[\w.@-]+$/u.test(extensionId) || !segments.length) {
    json(res, 400, { error: "Invalid extension path" });
    return;
  }
  if (segments.some((segment) => segment === "..")) {
    json(res, 403, { error: "Path escape" });
    return;
  }

  for (const root of EXTENSION_RESOURCE_ROOTS) {
    const extensionRoot = path.join(root, extensionId);
    const absolutePath = path.join(extensionRoot, ...segments);
    const relative = path.relative(extensionRoot, absolutePath);
    if (relative.startsWith("..") || path.isAbsolute(relative)) {
      continue;
    }
    const stats = await stat(absolutePath).catch(() => null);
    if (!stats?.isFile()) {
      continue;
    }
    await writeExtensionAssetBody(req, res, relativePath, await readFile(absolutePath), stats);
    return;
  }

  const asarPath = path.posix.join("resources", "extensions", extensionId, ...segments);
  const archived = await workBuddyAsar.statFile(asarPath);
  if (archived) {
    await writeExtensionAssetBody(req, res, relativePath, await workBuddyAsar.readFile(asarPath));
    return;
  }

  logger.warn("extension.asset.not_found", "Extension asset not found", {
    extensionId,
    relativePath,
  });
  json(res, 404, { error: "Extension asset not found", extensionId, path: relativePath });
}

async function sendAsarAsset(req, res, archive, relativePath, cacheControl) {
  const info = await archive.statFile(relativePath);
  if (!info) {
    json(res, 404, { error: "Not found" });
    return;
  }

  const contentType = contentTypeFor(relativePath);
  const lastModified = info.mtime.toUTCString();
  const baseHeaders = {
    "Content-Type": contentType,
    "Cache-Control": cacheControl,
    ETag: info.etag,
    "Last-Modified": lastModified,
  };

  if (isEtagMatch(req, info.etag) || isModifiedSinceMatch(req, info)) {
    writeNotModified(res, baseHeaders);
    return;
  }

  const encoding =
    info.size <= MAX_COMPRESSED_STATIC_ASSET_BYTES && isCompressibleAsset(relativePath)
      ? pickContentEncoding(req)
      : null;
  const stream = archive.createReadStream(info);
  if (!encoding) {
    res.writeHead(200, {
      ...baseHeaders,
      "Content-Length": info.size,
    });
    await pipeline(stream, res);
    return;
  }

  res.writeHead(200, {
    ...baseHeaders,
    "Content-Encoding": encoding,
    Vary: "Accept-Encoding",
  });
  await pipeline(stream, createCompressor(encoding), res);
}

function patchMainIndexLongSessionLoading(source) {
  let patched = source;

  const loadSessionTimeoutFrom =
    'const loadSessionWithTimeout = async (sessionId, targetCwd) => withTimeout(() => adapter.loadSession(sessionId, targetCwd, []), 15e3, `loadSession timeout for ${sessionId}`);';
  const loadSessionTimeoutTo =
    'const loadSessionWithTimeout = async (sessionId, targetCwd) => withTimeout(() => adapter.loadSession(sessionId, targetCwd, []), 95e3, `loadSession timeout for ${sessionId}`);';
  if (patched.includes(loadSessionTimeoutFrom)) {
    patched = patched.replace(loadSessionTimeoutFrom, loadSessionTimeoutTo);
  }

  const skipRedundantPreloadGetSessionFrom =
    "const preLoadSession = await api.getSession(sessionId);";
  const skipRedundantPreloadGetSessionTo =
    "const preLoadSession = effectiveCwd ? void 0 : await api.getSession(sessionId);";
  if (patched.includes(skipRedundantPreloadGetSessionFrom)) {
    patched = patched.replace(skipRedundantPreloadGetSessionFrom, skipRedundantPreloadGetSessionTo);
  }

  const addOrUpdateMessageFrom =
    'const addOrUpdateMessage = (0, import_react.useCallback)((conversationId, message) => {';
  const addOrUpdateMessageTo = `const sortConversationMessagesForRemoteHistory = (messages) => {
    if (!Array.isArray(messages) || messages.length <= 1) return messages;
    const readOrder = (message) => {
      const directOffset = message?._offset;
      const metaOffset = message?._meta?.["codebuddy.ai"]?.offset ?? message?.extra?.offset;
      const offset = Number(directOffset ?? metaOffset);
      if (Number.isFinite(offset)) return { kind: 0, value: offset };
      const created = Number(message?.createTime ?? message?.timestamp ?? 0);
      return Number.isFinite(created) && created > 0 ? { kind: 1, value: created } : { kind: 2, value: 0 };
    };
    if (!messages.some((message) => readOrder(message).kind !== 2)) return messages;
    return [...messages].sort((left, right) => {
      const leftOrder = readOrder(left);
      const rightOrder = readOrder(right);
      return leftOrder.kind - rightOrder.kind || leftOrder.value - rightOrder.value;
    });
  };
  const addOrUpdateMessage = (0, import_react.useCallback)((conversationId, message) => {`;
  if (patched.includes(addOrUpdateMessageFrom) && !patched.includes("sortConversationMessagesForRemoteHistory")) {
    patched = patched.replace(addOrUpdateMessageFrom, addOrUpdateMessageTo);
  }
  if (patched.includes("sortConversationMessagesForRemoteHistory")) {
    patched = patched.replaceAll(
      "else updatedMessages = [...conv.messages, message];",
      "else updatedMessages = sortConversationMessagesForRemoteHistory([...conv.messages, message]);"
    );
    patched = patched.replaceAll(
      "updatedMessages = [...prev.messages, message];",
      "updatedMessages = sortConversationMessagesForRemoteHistory([...prev.messages, message]);"
    );
    patched = patched.replace(
      "return { ...conv, messages: updatedMessages, lastMessage, timestamp: shouldPreserveConversationTimestamp ? conv.timestamp : /* @__PURE__ */ new Date() };",
      "return { ...conv, messages: updatedMessages, lastMessage: shouldPreserveConversationTimestamp ? conv.lastMessage : lastMessage, timestamp: shouldPreserveConversationTimestamp ? conv.timestamp : /* @__PURE__ */ new Date() };"
    );
  }

  const syncHistorySkipFrom =
    'syncSessionMessageSnapshot(sessionId, message) { if (!this.workbuddyApi) return; if (message.id.startsWith(`${sessionId}-response-`)) return;';
  const syncHistorySkipTo =
    'syncSessionMessageSnapshot(sessionId, message) { if (!this.workbuddyApi) return; if (message?.extra?.ownerHistoryMode === "history" || message?.extra?.workbuddyRemoteHistoryPage === true) return; if (message.id.startsWith(`${sessionId}-response-`)) return;';
  if (patched.includes(syncHistorySkipFrom)) {
    patched = patched.replace(syncHistorySkipFrom, syncHistorySkipTo);
  }
  const syncHistorySkipFormattedFrom =
    'syncSessionMessageSnapshot(sessionId, message) {\n\t\tif (!this.workbuddyApi) return;\n\t\tif (message.id.startsWith(`${sessionId}-response-`)) return;';
  const syncHistorySkipFormattedTo =
    'syncSessionMessageSnapshot(sessionId, message) {\n\t\tif (!this.workbuddyApi) return;\n\t\tif (message?.extra?.ownerHistoryMode === "history" || message?.extra?.workbuddyRemoteHistoryPage === true) return;\n\t\tif (message.id.startsWith(`${sessionId}-response-`)) return;';
  if (patched.includes(syncHistorySkipFormattedFrom)) {
    patched = patched.replace(syncHistorySkipFormattedFrom, syncHistorySkipFormattedTo);
  }

  const markHistoryFrom =
    'const nextMessage = fromWorkbuddySessionMessageSnapshot(snapshot); if (nextMessage.id.startsWith(`${sessionId}-response-`)) continue;';
  const markHistoryTo =
    'const nextMessage = fromWorkbuddySessionMessageSnapshot(snapshot); nextMessage.extra = { ...nextMessage.extra, ownerHistoryMode: "history", workbuddyRemoteHistoryPage: options?.workbuddyRemoteHistoryPage === true }; if (nextMessage.id.startsWith(`${sessionId}-response-`)) continue;';
  if (patched.includes(markHistoryFrom)) {
    patched = patched.replace(markHistoryFrom, markHistoryTo);
  }
  const markHistoryFormattedFrom =
    'const nextMessage = fromWorkbuddySessionMessageSnapshot(snapshot);\n\t\t\tif (nextMessage.id.startsWith(`${sessionId}-response-`)) continue;';
  const markHistoryFormattedTo =
    'const nextMessage = fromWorkbuddySessionMessageSnapshot(snapshot);\n\t\t\tnextMessage.extra = { ...nextMessage.extra, ownerHistoryMode: "history", workbuddyRemoteHistoryPage: options?.workbuddyRemoteHistoryPage === true };\n\t\t\tif (nextMessage.id.startsWith(`${sessionId}-response-`)) continue;';
  if (patched.includes(markHistoryFormattedFrom)) {
    patched = patched.replace(markHistoryFormattedFrom, markHistoryFormattedTo);
  }

  const registerPagerFrom =
    "this.reportHistoryLoadTiming(sessionId, monitorT1, msgCount); hydrationFinished = true;";
  const registerPagerTo =
    "this.reportHistoryLoadTiming(sessionId, monitorT1, msgCount); this.registerWorkbuddyRemoteHistoryPager(sessionId, loadedSessionSnapshot?.__workbuddyRemoteHistoryPage, ownerStatusAfterLoad); hydrationFinished = true;";
  if (patched.includes(registerPagerFrom) && !patched.includes("registerWorkbuddyRemoteHistoryPager(sessionId")) {
    patched = patched.replace(registerPagerFrom, registerPagerTo);
  }
  const registerPagerFormattedFrom =
    "this.reportHistoryLoadTiming(sessionId, monitorT1, msgCount);\n\t\t\t\thydrationFinished = true;";
  const registerPagerFormattedTo =
    "this.reportHistoryLoadTiming(sessionId, monitorT1, msgCount);\n\t\t\t\tthis.registerWorkbuddyRemoteHistoryPager(sessionId, loadedSessionSnapshot?.__workbuddyRemoteHistoryPage, ownerStatusAfterLoad);\n\t\t\t\thydrationFinished = true;";
  if (patched.includes(registerPagerFormattedFrom) && !patched.includes("registerWorkbuddyRemoteHistoryPager(sessionId")) {
    patched = patched.replace(registerPagerFormattedFrom, registerPagerFormattedTo);
  }

  const pagerMethodsFrom =
    "refreshExistingMessageFromRicherSnapshot(sessionId, existingMessage, snapshotMessage, ownerStatus) {";
  const pagerMethodsTo = `registerWorkbuddyRemoteHistoryPager(sessionId, pageInfo, ownerStatus) {
    this.__workbuddyRemoteHistoryPaging = this.__workbuddyRemoteHistoryPaging || new Map();
    if (!pageInfo?.hasMore || !this.workbuddyApi?.workbuddyRemoteGetSessionMessagesPage) {
      this.__workbuddyRemoteHistoryPaging.delete(sessionId);
      return;
    }
    this.__workbuddyRemoteHistoryPaging.set(sessionId, {
      before: Math.max(0, Number(pageInfo.offset) || 0),
      limit: Math.max(1, Number(pageInfo.limit) || 120),
      ownerStatus,
      loading: false,
      done: false
    });
    try {
      globalThis.__workbuddyRemoteLoadOlderHistory = (targetSessionId) => this.loadWorkbuddyRemoteOlderHistory(targetSessionId || this.currentActiveSessionId);
    } catch {}
  }
  async loadWorkbuddyRemoteOlderHistory(sessionId) {
    const state = this.__workbuddyRemoteHistoryPaging?.get(sessionId);
    if (!state || state.loading || state.done || state.before <= 0 || sessionId !== this.currentActiveSessionId) return false;
    state.loading = true;
    try {
      const page = await this.workbuddyApi.workbuddyRemoteGetSessionMessagesPage(sessionId, {
        before: state.before,
        limit: state.limit
      });
      const messages = Array.isArray(page?.messages) ? page.messages : [];
      if (messages.length === 0) {
        state.done = true;
        return false;
      }
      this.rehydrateOwnerMessages(sessionId, messages, {
        overwriteExisting: false,
        ownerStatus: state.ownerStatus,
        workbuddyRemoteHistoryPage: true
      });
      state.before = Math.max(0, Number(page?.offset) || 0);
      state.done = !page?.hasMore || state.before <= 0;
      return true;
    } catch (error) {
      console.warn("[WorkbuddyAdapter] load older remote history page failed:", error);
      return false;
    } finally {
      state.loading = false;
    }
  }
  ${pagerMethodsFrom}`;
  if (patched.includes(pagerMethodsFrom) && !patched.includes("loadWorkbuddyRemoteOlderHistory(sessionId)")) {
    patched = patched.replace(pagerMethodsFrom, pagerMethodsTo);
  }

  return patched;
}

async function sendMaybePatchedWorkBuddyAsset(req, res, archive, relativePath, cacheControl) {
  const normalizedPath = relativePath.replace(/\\/g, "/");
  const fileName = path.posix.basename(normalizedPath);
  const shouldPatchGrowthBuddy =
    /^setting-[\w-]+\.js$/u.test(fileName) &&
    normalizedPath.startsWith("renderer/assets/");
  const shouldPatchMainIndex =
    /^index-[\w-]+\.js$/u.test(fileName) &&
    normalizedPath.startsWith("renderer/assets/");

  if (!shouldPatchGrowthBuddy && !shouldPatchMainIndex) {
    await sendAsarAsset(req, res, archive, relativePath, cacheControl);
    return;
  }

  const source = await archive.readFile(relativePath);
  if (!source) {
    json(res, 404, { error: "Not found" });
    return;
  }

  const original = source.toString("utf8");
  const growthBuddyMissingFrom =
    'console.warn("[useGrowthBuddy] adapter.getGrowthBuddy is not available");\n\t\t\tsetError("Growth buddy feature is not available");';
  const growthBuddyMissingTo =
    'console.info("[useGrowthBuddy] growth buddy feature is unavailable in this remote bridge");\n\t\t\tfetchedRef.current = true;\n\t\t\tsetError(null);\n\t\t\treturn;';
  const wecomStatusUnhandledFrom =
    'if (event.startsWith("memory-") || event.includes("memory")) return;\n\t\tif (event === "sandbox-intercept-request") {';
  const wecomStatusUnhandledTo =
    'if (event === "wecom-status") return;\n\t\tif (event.startsWith("memory-") || event.includes("memory")) return;\n\t\tif (event === "sandbox-intercept-request") {';
  let patched = original;
  if (shouldPatchGrowthBuddy && patched.includes(growthBuddyMissingFrom)) {
    patched = patched.replace(growthBuddyMissingFrom, growthBuddyMissingTo);
  }
  if (shouldPatchMainIndex && patched.includes(wecomStatusUnhandledFrom)) {
    patched = patched.replace(wecomStatusUnhandledFrom, wecomStatusUnhandledTo);
  }
  if (shouldPatchMainIndex) {
    const sidebarToggleExposeFrom =
      'const applySmartSpaceLayout = (0, import_react.useCallback)((options) => {';
    const sidebarToggleExposeTo = `try {
      const __workbuddyRemoteAnimateSidebar = () => {
        const gridEl = gridRef.current?.element ?? document.querySelector(".teams-container");
        if (gridEl) {
          gridEl.classList.add("sidebar-animating");
          setTimeout(() => gridEl.classList.remove("sidebar-animating"), 300);
        }
      };
      const __workbuddyRemoteDrawerConfig = () => ({
        placement: "left",
        size: SIDEBAR_SIZE.EXPANDED_WIDTH,
        showBackdrop: true,
        animationDuration: 300
      });
      globalThis.__workbuddyRemoteSetSidebarOpen = (open) => {
        const nextOpen = Boolean(open);
        const grid = gridRef.current;
        const view = sidebarGridViewRef.current;
        if (!grid || !view) return false;
        const viewportWidth = typeof window !== "undefined" ? window.innerWidth || 0 : 0;
        const runtimeNarrowForSidebar = Boolean(isNarrowForSidebar || (viewportWidth > 0 && viewportWidth <= 820));
        const runtimeNarrowForDetail = Boolean(isNarrowForDetail || (viewportWidth > 0 && viewportWidth <= 980));
        if (isLocalMode) {
          __workbuddyRemoteAnimateSidebar();
          if (nextOpen) {
            view.minimumWidth = SIDEBAR_SIZE.EXPANDED_WIDTH;
            view.maximumWidth = SIDEBAR_SIZE.EXPANDED_WIDTH;
            grid.setViewVisible(view, true);
            setSidebarExpanded(true);
            setWorkbuddyHidden(false);
          } else {
            grid.setViewVisible(view, false);
            setWorkbuddyHidden(true);
          }
          try { localStorage.setItem(SIDEBAR_STORAGE_KEY, String(nextOpen)); } catch {}
          return true;
        }
        if (runtimeNarrowForSidebar) {
          if (nextOpen) {
            if (runtimeNarrowForDetail && detailPanelViewRef.current) {
              try {
                if (grid.isDrawerOpen?.(detailPanelViewRef.current)) grid.closeDrawer(detailPanelViewRef.current);
              } catch {}
            }
            if (typeof grid.openDrawer === "function") {
              grid.openDrawer(view, __workbuddyRemoteDrawerConfig());
            } else if (typeof grid.toggleDrawer === "function" && !grid.isDrawerOpen?.(view)) {
              grid.toggleDrawer(view, __workbuddyRemoteDrawerConfig());
            }
          } else if (typeof grid.closeDrawer === "function") {
            try { grid.closeDrawer(view); } catch {}
          }
          return true;
        }
        __workbuddyRemoteAnimateSidebar();
        if (nextOpen && !canExpandSidebar()) return false;
        userWantsSidebarExpandedRef.current = nextOpen;
        isAutoCollapsedRef.current = false;
        try { localStorage.setItem(SIDEBAR_STORAGE_KEY, String(nextOpen)); } catch {}
        setSidebarExpanded(nextOpen);
        return true;
      };
      globalThis.__workbuddyRemoteOpenSidebar = () => globalThis.__workbuddyRemoteSetSidebarOpen(true);
      globalThis.__workbuddyRemoteCloseSidebar = () => globalThis.__workbuddyRemoteSetSidebarOpen(false);
      globalThis.__workbuddyRemoteToggleSidebar = handleToggleSidebar;
      globalThis.__workbuddyRemoteGetSidebarState = () => {
        let drawerOpen = false;
        try { drawerOpen = Boolean(gridRef.current?.isDrawerOpen?.(sidebarGridViewRef.current)); } catch {}
        return {
          open: isLocalMode ? !workbuddyHidden : isNarrowForSidebar ? drawerOpen : sidebarExpanded,
          collapsed: isLocalMode ? workbuddyHidden : !sidebarExpanded,
          narrow: isNarrowForSidebar,
          local: isLocalMode
        };
      };
    } catch {} const applySmartSpaceLayout = (0, import_react.useCallback)((options) => {`;
    if (patched.includes(sidebarToggleExposeFrom) && !patched.includes("__workbuddyRemoteToggleSidebar")) {
      patched = patched.replace(sidebarToggleExposeFrom, sidebarToggleExposeTo);
    }
    const detailToggleExposeFrom = "\treturn {\n\t\tgridRef,";
    const detailToggleExposeTo = `\tconst __workbuddyRemoteToggleDetailPanel = () => {
      const grid = gridRef.current;
      const view = detailPanelViewRef.current;
      if (!grid || !view) return false;
      try {
        if (isNarrowForDetail) {
          setShowDetailPanel(grid.toggleDrawer(view, detailDrawerConfig));
        } else {
          setShowDetailPanel(grid.toggleViewVisible(view));
        }
        return true;
      } catch {
        return false;
      }
    };
    try {
      globalThis.__workbuddyRemoteToggleDetailPanel = __workbuddyRemoteToggleDetailPanel;
    } catch {}
\treturn {
\t\tgridRef,`;
    if (patched.includes(detailToggleExposeFrom) && !patched.includes("__workbuddyRemoteToggleDetailPanel")) {
      patched = patched.replace(detailToggleExposeFrom, detailToggleExposeTo);
    }
    patched = patchMainIndexLongSessionLoading(patched);
  }

  sendVersionedScript(req, res, patched, {
    etag: `"workbuddy-asset-${fileName}-${patched.length}"`,
    cacheControl,
  });
}

function sendVersionedScript(req, res, payload, { etag, cacheControl }) {
  const body = Buffer.from(payload, "utf8");
  const contentType = "application/javascript; charset=utf-8";
  const baseHeaders = {
    "Content-Type": contentType,
    "Cache-Control": cacheControl,
    ETag: etag,
  };

  if (isEtagMatch(req, etag)) {
    writeNotModified(res, baseHeaders);
    return;
  }

  const encoding = body.byteLength <= MAX_COMPRESSED_STATIC_ASSET_BYTES ? pickContentEncoding(req) : null;
  if (!encoding) {
    writeBuffer(res, 200, body, baseHeaders);
    return;
  }

  const cacheKey = `${etag}:${encoding}:${body.byteLength}`;
  let encodedBody = versionedScriptCompressionCache.get(cacheKey);
  if (!encodedBody) {
    encodedBody = compressBufferSync(body, encoding);
    versionedScriptCompressionCache.set(cacheKey, encodedBody);
  }

  writeBuffer(res, 200, encodedBody, {
    ...baseHeaders,
    "Content-Encoding": encoding,
    Vary: "Accept-Encoding",
  });
}

function isLoopbackHost(host) {
  const value = String(host || "").trim().toLowerCase();
  return value === "127.0.0.1" || value === "::1" || value === "localhost";
}

function getAccessUrls(options) {
  if (isLoopbackHost(options.listenHost)) {
    return [`http://127.0.0.1:${options.listenPort}/agent-manager/`];
  }

  if (options.listenHost === "0.0.0.0" || options.listenHost === "::") {
    return [
      `http://127.0.0.1:${options.listenPort}/agent-manager/`,
      ...getLanUrls(options.listenPort),
    ];
  }

  return [`http://${options.listenHost}:${options.listenPort}/agent-manager/`];
}

function withFaviconLinks(html) {
  if (typeof html !== "string" || !html) {
    return html;
  }
  if (/rel=["'](?:shortcut )?icon["']/iu.test(html)) {
    return html;
  }
  const headMatch = /<head[^>]*>/iu.exec(html);
  if (!headMatch) {
    return FAVICON_LINK_TAGS + html;
  }
  const insertAt = headMatch.index + headMatch[0].length;
  return html.slice(0, insertAt) + FAVICON_LINK_TAGS + html.slice(insertAt);
}

async function listWindowsDrives() {
  const drives = [];
  const letters = "CDEFGHIJKLMNOPQRSTUVWXYZ";
  await Promise.all(
    [...letters].map(async (letter) => {
      const root = `${letter}:\\`;
      try {
        const info = await stat(root);
        if (info.isDirectory()) {
          drives.push({ name: `${letter}:`, path: root });
        }
      } catch {
        // 盘符不存在或不可访问
      }
    })
  );
  return drives.sort((a, b) => a.name.localeCompare(b.name));
}

async function buildRemoteFsRoots() {
  const home = os.homedir();
  const quick = [{ name: "主目录", path: home }];
  for (const [label, folder] of [
    ["桌面", "Desktop"],
    ["文档", "Documents"],
    ["下载", "Downloads"],
    ["图片", "Pictures"],
    ["视频", "Videos"],
  ]) {
    const target = path.join(home, folder);
    try {
      if ((await stat(target)).isDirectory()) {
        quick.push({ name: label, path: target });
      }
    } catch {
      // 该用户目录不存在
    }
  }
  return { home, quick, drives: await listWindowsDrives() };
}

async function readRemoteDirectory(targetPath) {
  const dir = targetPath && String(targetPath).trim() ? path.resolve(String(targetPath).trim()) : os.homedir();
  const dirents = await readdir(dir, { withFileTypes: true });
  const entries = [];
  for (const dirent of dirents) {
    const full = path.join(dir, dirent.name);
    let isDir = dirent.isDirectory();
    let size = 0;
    let mtime = 0;
    try {
      const info = await stat(full);
      isDir = info.isDirectory();
      size = info.size;
      mtime = info.mtimeMs;
    } catch {
      // 权限不足的条目也列出来，只是没有大小/时间
    }
    entries.push({ name: dirent.name, path: full, isDir, size, mtime });
  }
  entries.sort((a, b) => Number(b.isDir) - Number(a.isDir) || a.name.localeCompare(b.name, "zh-CN"));
  const parent = path.dirname(dir);
  return { path: dir, parent: parent === dir ? null : parent, entries };
}

function summarizeHttpChannelArgs(args) {
  if (!Array.isArray(args)) {
    return args;
  }
  return args.slice(0, 3).map((item) => {
    if (typeof item === "string") {
      return item.length > 300 ? item.slice(0, 300) + "…" : item;
    }
    if (!item || typeof item !== "object") {
      return item;
    }
    const out = {};
    for (const [key, value] of Object.entries(item)) {
      if (/authorization|cookie|token/i.test(key)) {
        out[key] = "[redacted]";
        continue;
      }
      if (typeof value === "string") {
        out[key] = value.length > 400 ? value.slice(0, 400) + "…" : value;
      } else if (value && typeof value === "object") {
        try {
          const json = JSON.stringify(value);
          out[key] = json.length > 400 ? json.slice(0, 400) + "…" : json;
        } catch {
          out[key] = "[object]";
        }
      } else {
        out[key] = value;
      }
    }
    return out;
  });
}

function createRequestHandler(runtime, auth) {
  return async (req, res) => {
    try {
      const requestUrl = new URL(req.url || "/", "http://bridge.local");

      if (await auth.handleRequest(req, res, requestUrl)) {
        return;
      }

      if (await handleWorkDaddyRequest(req, res, requestUrl)) {
        return;
      }

      if (requestUrl.pathname === "/") {
        res.writeHead(302, { Location: "/agent-manager/" });
        res.end();
        return;
      }

      if (requestUrl.pathname === "/healthz") {
        json(res, 200, { ok: true });
        return;
      }

      if (requestUrl.pathname === "/readyz") {
        const ready = runtime.isHostConnected();
        json(res, ready ? 200 : 503, {
          ok: ready,
          hostConnected: ready,
        });
        return;
      }

      if (requestUrl.pathname.startsWith(LOOPBACK_PROXY_PREFIX)) {
        const targetUrl = parseLoopbackProxyTarget(requestUrl);
        if (!targetUrl) {
          json(res, 400, { ok: false, error: "Invalid loopback proxy URL." });
          return;
        }

        await proxyLoopbackRequest(req, res, targetUrl);
        return;
      }

      const refererLoopbackTarget = parseLoopbackProxyTargetFromReferer(
        requestUrl,
        req.headers.referer
      );
      if (refererLoopbackTarget) {
        await proxyLoopbackRequest(req, res, refererLoopbackTarget);
        return;
      }

      if (requestUrl.pathname === "/bridge/bootstrap") {
        const features = await loadFeatureFlags();
        json(res, 200, {
          ok: true,
          hostConnected: runtime.isHostConnected(),
          enableFileManager: features.enableFileManager,
          restartAvailable: features.enableRestart && runtime.canRestartCurrentApp(),
        });
        return;
      }

      if (requestUrl.pathname === "/bridge/workspace-roots") {
        if (!(await isFileManagerEnabled())) {
          writeFeatureDisabled(res, "File manager");
          return;
        }
        json(res, 200, {
          ok: true,
          roots: await listAvailableWorkspaceRoots(),
        });
        return;
      }

      if (requestUrl.pathname === "/bridge/workspace-context") {
        if (!(await isFileManagerEnabled())) {
          writeFeatureDisabled(res, "File manager");
          return;
        }
        json(res, 200, {
          ok: true,
          paths: await runtime.getWorkspaceContextCandidates(),
        });
        return;
      }

      if (requestUrl.pathname === "/bridge/workspace-folders") {
        if (!(await isFileManagerEnabled())) {
          writeFeatureDisabled(res, "File manager");
          return;
        }
        if (req.method === "GET") {
          const result = await listWorkspaceFolders(requestUrl.searchParams.get("rootPath"));
          json(res, 200, {
            ok: true,
            ...result,
          });
          return;
        }

        if (req.method === "POST") {
          const payload = await readJsonBody(req);
          json(res, 200, await createWorkspaceFolder(payload?.rootPath, payload?.name));
          return;
        }

        if (req.method === "PATCH") {
          const payload = await readJsonBody(req);
          json(res, 200, await renameWorkspaceFolder(payload?.folderPath, payload?.name));
          return;
        }

        if (req.method === "DELETE") {
          const payload = await readJsonBody(req);
          json(res, 200, await deleteWorkspaceFolder(payload?.folderPath));
          return;
        }

        json(res, 405, { ok: false, error: "Method not allowed" });
        return;
      }

      if (requestUrl.pathname === "/bridge/workspace-files") {
        if (!(await isFileManagerEnabled())) {
          writeFeatureDisabled(res, "File manager");
          return;
        }
        if (req.method === "GET") {
          json(res, 200, await listWorkspaceEntries(requestUrl.searchParams.get("folderPath")));
          return;
        }

        if (req.method === "POST") {
          const uploadId = requestUrl.searchParams.get("uploadId");
          const totalBytes = Number(req.headers["content-length"] || 0);
          let lastProgressAt = 0;
          const onProgress = uploadId
            ? (loadedBytes) => {
                const now = Date.now();
                if (loadedBytes < totalBytes && now - lastProgressAt < 150) {
                  return;
                }
                lastProgressAt = now;
                runtime.broadcast({
                  type: "workspace-upload-progress",
                  uploadId,
                  loadedBytes,
                  totalBytes,
                });
              }
            : undefined;

          const result = await uploadWorkspaceFile(
            requestUrl.searchParams.get("folderPath"),
            requestUrl.searchParams.get("fileName"),
            req,
            onProgress
          );
          onProgress?.(totalBytes);
          json(res, 200, result);
          return;
        }

        if (req.method === "DELETE") {
          const payload = await readJsonBody(req);
          json(res, 200, await deleteWorkspaceEntry(payload?.targetPath));
          return;
        }

        json(res, 405, { ok: false, error: "Method not allowed" });
        return;
      }

      if (requestUrl.pathname === "/bridge/workspace-download") {
        if (!(await isFileManagerEnabled())) {
          writeFeatureDisabled(res, "File manager");
          return;
        }
        if (req.method !== "GET") {
          json(res, 405, { ok: false, error: "Method not allowed" });
          return;
        }

        const { normalizedPath, stats } = await resolveWorkspaceFilePath(
          requestUrl.searchParams.get("targetPath")
        );
        const fileName = path.win32.basename(normalizedPath);
        res.writeHead(200, {
          "Content-Type": contentTypeFor(normalizedPath),
          "Cache-Control": NO_STORE_CACHE_CONTROL,
          "Content-Length": stats.size,
          "Content-Disposition": `attachment; filename*=UTF-8''${encodeURIComponent(fileName)}`,
        });
        await pipeline(createReadStream(normalizedPath), res);
        return;
      }

      // 站点图标：优先用 WorkBuddy 自带的 .ico（Content-Type 正确），
      // 同时提供 PNG 与 apple-touch-icon，避免部分浏览器/端口映射代理
      // 因为 MIME 不符或缺少 apple-touch-icon 而显示成空白页图标。
      if (FAVICON_PATHS.has(requestUrl.pathname)) {
        const iconPath =
          requestUrl.pathname === "/favicon.ico" ? "resources/icon.ico" : "resources/icon.png";
        await sendAsarAsset(req, res, workBuddyAsar, iconPath, FAVICON_CACHE_CONTROL);
        return;
      }

      if (requestUrl.pathname === "/agent-manager") {
        res.writeHead(302, { Location: "/agent-manager/" });
        res.end();
        return;
      }

      if (requestUrl.pathname === "/agent-manager/") {
        const html = await workBuddyAsar.readFile("renderer/index.html");
        if (!html) {
          throw new Error(`WorkBuddy renderer/index.html was not found in ${WORKBUDDY_ASAR_PATH}`);
        }
        text(
          res,
          200,
          renderWorkBuddyNativeHtml(withFaviconLinks(html.toString("utf8"))),
          "text/html; charset=utf-8",
          HTML_CACHE_CONTROL
        );
        return;
      }

      // 服务端目录浏览：给网页端“选择文件夹”对话框用。
      if (requestUrl.pathname.startsWith("/bridge/fs/")) {
        try {
          if (requestUrl.pathname === "/bridge/fs/roots") {
            json(res, 200, { ok: true, roots: await listRoots() });
            return;
          }
          if (requestUrl.pathname === "/bridge/fs/list") {
            const target = requestUrl.searchParams.get("path") || "";
            const listing = await listDirectory(target);
            json(res, 200, { ok: true, ...listing });
            return;
          }
          if (requestUrl.pathname === "/bridge/fs/mkdir" && req.method === "POST") {
            const payload = await readJsonBody(req);
            const created = await makeDirectory(payload.path, payload.name);
            json(res, 200, { ok: true, path: created });
            return;
          }
          json(res, 404, { ok: false, error: "unknown fs endpoint" });
        } catch (error) {
          json(res, 400, {
            ok: false,
            error: error instanceof Error ? error.message : String(error),
          });
        }
        return;
      }

      if (requestUrl.pathname === "/bridge/workbuddy-native-shim.js") {        const [methods, version, locale, features, bridgeUiConfig] = await Promise.all([
          readShimMetadata("methods", () => runtime.getBuddyApiMethods(), []),
          readShimMetadata("version", () => runtime.getWorkBuddyVersion(), ""),
          readShimMetadata("locale", () => runtime.getWorkBuddyLocale(), ""),
          loadFeatureFlags(),
          loadBridgeUiConfig(),
        ]);
        const shim = renderWorkBuddyNativeShimJs({
          methods,
          version,
          locale,
          ...features,
          ...bridgeUiConfig,
        });
        sendVersionedScript(
          req,
          res,
          shim,
          {
            etag: `"workbuddy-native-shim-${version || "unknown"}-${locale || "unknown"}-${methods.length}-${shim.length}-${features.enableFileManager ? "files-on" : "files-off"}-${features.enableRestart ? "restart-on" : "restart-off"}-${bridgeUiConfig.maskBridgeModelSecrets ? "secrets-masked" : "secrets-visible"}"`,
            cacheControl: REVALIDATED_STATIC_CACHE_CONTROL,
          }
        );
        return;
      }

      if (requestUrl.pathname.startsWith(EXTENSION_HTTP_BASE)) {
        await sendExtensionAsset(req, res, requestUrl);
        return;
      }

      if (requestUrl.pathname.startsWith("/agent-manager/assets/")) {
        const relativePath = decodeURIComponent(
          requestUrl.pathname.replace("/agent-manager/", "renderer/")
        );
        await sendMaybePatchedWorkBuddyAsset(
          req,
          res,
          workBuddyAsar,
          relativePath,
          IMMUTABLE_CACHE_CONTROL
        );
        return;
      }

      if (requestUrl.pathname.startsWith("/agent-manager/")) {
        const relativePath = decodeURIComponent(
          requestUrl.pathname.replace("/agent-manager/", "renderer/")
        );
        const info = await workBuddyAsar.statFile(relativePath);
        if (info) {
          await sendMaybePatchedWorkBuddyAsset(
            req,
            res,
            workBuddyAsar,
            relativePath,
            IMMUTABLE_CACHE_CONTROL
          );
          return;
        }
      }

      json(res, 404, { error: "Not found" });
    } catch (error) {
      if (res.headersSent) {
        res.destroy(error);
        return;
      }

      json(res, 500, {
        error: error instanceof Error ? error.message : String(error),
      });
    }
  };
}

function attachWebSocketServer(server, runtime, auth) {
  const wss = new WebSocketServer({
    noServer: true,
    maxPayload: BROWSER_WS_MAX_PAYLOAD_BYTES,
  });
  const modelSecretProtector = createModelSecretProtector();

  wss.on("connection", (socket) => {
    logger.info("websocket.connection", "Browser WebSocket connected", {
      remoteAddress: socket?._socket?.remoteAddress,
      remotePort: socket?._socket?.remotePort,
    });
    runtime.registerBrowserSocket(socket);

    socket.on("close", (code, reason) => {
      incomingChunks.clear();
      logger.info("websocket.close", "Browser WebSocket closed", {
        code,
        reason: reason ? reason.toString() : "",
      });
    });

    socket.on("error", (error) => {
      logger.warn("websocket.error", "Browser WebSocket reported an error", { error });
    });

    const incomingChunks = new Map();

    const handleBrowserMessage = async (message) => {
      logger.debug("websocket.browser_to_bridge", "Browser sent bridge message", {
        message: summarizeMessage(message),
      });

      try {
        if (message.type === "desktop-host-invoke") {
          logger.info("desktop_host.invoke", "Forwarding desktop host channel", {
            channel: message.channel,
            via: message.via || "",
            argCount: Array.isArray(message.args) ? message.args.length : 0,
          });
          if (String(message.channel || "").startsWith("wb:http:")) {
            logger.debug("desktop_host.http", "Forwarding host http channel", {
              channel: message.channel,
              args: summarizeHttpChannelArgs(message.args),
            });
          }
          const result = await runtime.invokeDesktopHost(
            message.channel,
            message.args || [],
            message.via || ""
          );
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result,
          });
          return;
        }

        if (message.type === "desktop-terminal-call") {
          const result = await runtime.callDesktopTerminal(
            message.method,
            message.args || []
          );
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result,
          });
          return;
        }

        if (message.type === "desktop-host-events-subscribe") {
          const subscribed = await runtime.subscribeDesktopEvents(socket);
          if (message.id) {
            runtime.sendToSocket(socket, { id: message.id, ok: true, result: { subscribed } });
          }
          return;
        }

        if (message.type === "daemon-open") {
          const result = await runtime.openDaemonTransport(socket);
          if (message.id) {
            runtime.sendToSocket(socket, { id: message.id, ok: true, result });
          }
          return;
        }

        if (message.type === "daemon-send") {
          const frame = message.frame || {};
          let preview = "";
          try {
            preview = String(frame.json || "").slice(0, 220);
          } catch {
            preview = "";
          }
          logger.debug("daemon.send", "Browser sent daemon frame", {
            kind: frame.kind || "",
            preview,
          });
          const result = await runtime.sendDaemonFrame(socket, message.frame);
          if (message.id) {
            runtime.sendToSocket(socket, { id: message.id, ok: true, result });
          }
          return;
        }

        if (message.type === "daemon-close") {
          const result = await runtime.closeDaemonTransport(socket);
          if (message.id) {
            runtime.sendToSocket(socket, { id: message.id, ok: true, result });
          }
          return;
        }

        if (message.type === "buddy-api-call") {
          const bridgeUiConfig = await loadBridgeUiConfig();
          const shouldProtectModelSecrets = bridgeUiConfig.maskBridgeModelSecrets === true;
          const args = shouldProtectModelSecrets
            ? modelSecretProtector.restoreArgs(message.method, message.args || [])
            : message.args || [];
          const rawResult = await runtime.invokeBuddyApi(message.method, args);
          const result = shouldProtectModelSecrets
            ? modelSecretProtector.protectResult(message.method, args, rawResult)
            : rawResult;
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result,
          });
          return;
        }

        if (message.type === "buddy-api-call-discard-result") {
          const bridgeUiConfig = await loadBridgeUiConfig();
          const shouldProtectModelSecrets = bridgeUiConfig.maskBridgeModelSecrets === true;
          const args = shouldProtectModelSecrets
            ? modelSecretProtector.restoreArgs(message.method, message.args || [])
            : message.args || [];
          const result = await runtime.invokeBuddyApiDiscardResult(message.method, args);
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result,
          });
          return;
        }

        if (message.type === "buddy-api-call-session-page") {
          const bridgeUiConfig = await loadBridgeUiConfig();
          const shouldProtectModelSecrets = bridgeUiConfig.maskBridgeModelSecrets === true;
          const args = shouldProtectModelSecrets
            ? modelSecretProtector.restoreArgs(message.method, message.args || [])
            : message.args || [];
          const rawResult = await runtime.invokeBuddyApiSessionPage(message.method, args, {
            limit: message.limit,
          });
          const result = shouldProtectModelSecrets
            ? modelSecretProtector.protectResult(message.method, args, rawResult)
            : rawResult;
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result,
          });
          return;
        }

        if (message.type === "workbuddy-remote-session-messages-page") {
          const result = await runtime.getStoredSessionMessagesPage(message.sessionId, {
            before: message.before,
            limit: message.limit,
          });
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result,
          });
          return;
        }

        if (message.type === "buddy-api-subscribe") {
          await runtime.subscribeBuddyApi(
            message.method,
            message.key || message.method,
            message.args || [],
            socket
          );
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result: true,
          });
          return;
        }

        if (message.type === "buddy-api-unsubscribe") {
          await runtime.unsubscribeBuddyApi(message.key || message.method, socket);
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result: true,
          });
          return;
        }

        if (message.type === "restart-app") {
          if (!(await isRestartEnabled())) {
            throw new Error("Restart is disabled by config.");
          }
          const result = await runtime.requestRestart();
          runtime.sendToSocket(socket, {
            id: message.id,
            ok: true,
            result,
          });
          return;
        }

        runtime.sendToSocket(socket, {
          id: message.id,
          ok: false,
          error: `Unsupported bridge message type: ${message.type || "(missing)"}`,
        });
      } catch (error) {
        logger.error("websocket.message.error", "Failed to handle browser bridge message", {
          message: summarizeMessage(message),
          error,
        });
        runtime.sendToSocket(socket, {
          id: message.id,
          ok: false,
          error: error instanceof Error ? error.message : String(error),
        });
      }
    };

    const handleChunkFrame = (message) => {
      if (message.type === "bridge-client-message-chunk-start") {
        incomingChunks.set(message.transferId, {
          chunks: [],
          totalChunks: Math.max(0, Math.trunc(Number(message.totalChunks) || 0)),
          totalLength: Math.max(0, Math.trunc(Number(message.totalLength) || 0)),
          received: 0,
        });
        return true;
      }

      if (message.type === "bridge-client-message-chunk") {
        const entry = incomingChunks.get(message.transferId);
        if (!entry) {
          logger.warn("websocket.browser_to_bridge.chunk_missing", "Browser WebSocket chunk had no start frame", {
            transferId: message.transferId,
            index: message.index,
          });
          return true;
        }
        const index = Math.trunc(Number(message.index) || 0);
        const data = typeof message.data === "string" ? message.data : "";
        entry.chunks[index] = data;
        entry.received += 1;
        return true;
      }

      if (message.type !== "bridge-client-message-chunk-end") {
        return false;
      }

      const entry = incomingChunks.get(message.transferId);
      incomingChunks.delete(message.transferId);
      if (!entry) {
        logger.warn("websocket.browser_to_bridge.chunk_end_missing", "Browser WebSocket chunk end had no start frame", {
          transferId: message.transferId,
        });
        return true;
      }

      const raw = entry.chunks.join("");
      if (entry.totalLength && raw.length !== entry.totalLength) {
        logger.warn("websocket.browser_to_bridge.chunk_length_mismatch", "Browser WebSocket chunked message length differed", {
          transferId: message.transferId,
          expected: entry.totalLength,
          actual: raw.length,
        });
      }

      let completed;
      try {
        completed = JSON.parse(raw);
      } catch {
        logger.warn("websocket.browser_to_bridge.invalid_chunked_json", "Invalid chunked browser WebSocket payload", {
          transferId: message.transferId,
          bytes: raw.length,
        });
        runtime.sendToSocket(socket, {
          ok: false,
          error: "Invalid chunked JSON",
        });
        return true;
      }

      logger.info("websocket.browser_to_bridge.chunked_receive", "Received chunked browser WebSocket message", {
        transferId: message.transferId,
        totalLength: raw.length,
        totalChunks: entry.totalChunks,
        receivedChunks: entry.received,
        message: summarizeMessage(completed),
      });
      handleBrowserMessage(completed).catch((error) => {
        logger.error("websocket.browser_to_bridge.chunked_handle_error", "Failed to handle chunked browser WebSocket message", {
          transferId: message.transferId,
          error,
        });
      });
      return true;
    };

    socket.on("message", async (raw) => {
      let message;
      try {
        message = JSON.parse(raw.toString());
      } catch {
        logger.warn("websocket.browser_to_bridge.invalid_json", "Invalid browser WebSocket payload", {
          bytes: raw?.byteLength ?? raw?.length,
        });
        runtime.sendToSocket(socket, {
          ok: false,
          error: "Invalid JSON",
        });
        return;
      }

      if (handleChunkFrame(message)) {
        return;
      }

      await handleBrowserMessage(message);
    });
  });

  server.on("upgrade", (req, socket, head) => {
    const requestUrl = new URL(req.url || "/", "http://bridge.local");
    if (requestUrl.pathname !== "/bridge/ws") {
      socket.destroy();
      return;
    }

    if (!auth.handleUpgrade(req, socket)) {
      return;
    }

    wss.handleUpgrade(req, socket, head, (webSocket) => {
      wss.emit("connection", webSocket, req);
    });
  });

  return wss;
}

async function startBridgeServer(runtime, options) {
  const auth = createBridgeAccessAuth(options.passwordHash);
  if (!auth.enabled && !isLoopbackHost(options.listenHost)) {
    throw new Error(
      "A password hash is required when the bridge listens beyond localhost. Set --password-hash or WORKBUDDY_REMOTE_PASSWORD_HASH first."
    );
  }

  const server = http.createServer(createRequestHandler(runtime, auth));
  server.requestTimeout = 0;
  server.setTimeout(0);
  attachWebSocketServer(server, runtime, auth);

  await new Promise((resolve, reject) => {
    const onError = (error) => {
      server.off("listening", onListening);
      reject(error);
    };
    const onListening = () => {
      server.off("error", onError);
      resolve();
    };
    server.once("error", onError);
    server.once("listening", onListening);
    server.listen(options.listenPort, options.listenHost);
  });

  logger.info("bridge.server.started", "Bridge HTTP/WebSocket server started", {
    cdpTarget: `ws://${options.cdpHost}:${options.cdpPort}`,
    listenHost: options.listenHost,
    listenPort: options.listenPort,
    workbuddyPid: options.workbuddyPid,
    logPath: options.logPath,
    asarPath: WORKBUDDY_ASAR_PATH,
  });
  if (auth.enabled) {
    logger.info("bridge.auth", "Password protection enabled");
  } else {
    logger.info("bridge.auth", "Password protection disabled");
  }

  const urls = getAccessUrls(options);
  if (urls.length > 0) {
    logger.info("bridge.url.primary", "Primary browser URL available", { url: urls[0] });
  }
  for (const url of urls.slice(1)) {
    logger.info("bridge.url.additional", "Additional browser URL available", { url });
  }
  return server;
}

export { startBridgeServer };
