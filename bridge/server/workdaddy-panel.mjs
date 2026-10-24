import { existsSync } from "node:fs";
import fs from "node:fs/promises";
import path from "node:path";

/**
 * 把 WorkDaddy 自己的面板（inject.js + 兼容层 + toast + markdown）按 daemon 的拼装规则
 * 构建成一份可在浏览器里执行的脚本：
 *   - __WBS_API__ 改成同源前缀 /workdaddy，请求由桥接代理到本机 daemon；
 *   - __WBS_API_TOKEN__ 填占位值，真实令牌由代理在服务端补上（不暴露给浏览器）；
 *   - 其余占位符（版本、profile、caps、logo、platform）按 workbuddy-cn 的配置填充。
 * 这样网页端拿到的是与桌面端完全一致的面板与功能，而不是简化版。
 */

const PANEL_ANCHOR = "return { destroy: lifecycle.destroy, alive: lifecycle.alive };";
const PANEL_CAPS = {
  accounts: true,
  sessions: true,
  models: true,
  stashPrompt: true,
  theme: true,
  checkin: true,
  growthDaily: true,
  appName: "WorkDaddy",
};

const cache = { key: "", etag: "", source: "" };

function candidateHomes() {
  const env = process.env.WORKDADDY_HOME;
  const list = [];
  if (env) list.push(env);
  const localAppData = process.env.LOCALAPPDATA || "";
  const programFiles = process.env.ProgramFiles || "C:\\Program Files";
  const programFilesX86 = process.env["ProgramFiles(x86)"] || "C:\\Program Files (x86)";
  if (localAppData) list.push(path.join(localAppData, "Programs", "WorkDaddy"));
  list.push(path.join(programFiles, "WorkDaddy"));
  list.push(path.join(programFilesX86, "WorkDaddy"));
  // 开发机常见位置，便于本地调试
  list.push("D:\\Program Files\\Dev\\WorkDaddy");
  return list;
}

export function resolveWorkDaddyHome() {
  for (const home of candidateHomes()) {
    if (!home) continue;
    if (existsSync(path.join(home, "scripts", "inject.js"))) {
      return home;
    }
  }
  return null;
}

function resolveLauncherPath(home) {
  if (!home) return "";
  for (const name of ["WorkDaddyLauncher.exe", "WorkDaddy.exe"]) {
    const candidate = path.join(home, name);
    if (existsSync(candidate)) return candidate;
  }
  return "";
}

export function describeWorkDaddyInstall() {
  const home = resolveWorkDaddyHome();
  if (!home) return { installed: false, home: "", launcher: "" };
  return { installed: true, home, launcher: resolveLauncherPath(home) };
}

async function readDaemonVersion() {
  try {
    const appData = process.env.APPDATA || "";
    if (!appData) return "";
    const raw = await fs.readFile(path.join(appData, "WorkDaddy", ".daemon.lock"), "utf8");
    const parsed = JSON.parse(raw);
    return typeof parsed.version === "string" ? parsed.version : "";
  } catch {
    return "";
  }
}

async function fileMtime(file) {
  try {
    const info = await fs.stat(file);
    return info.mtimeMs;
  } catch {
    return 0;
  }
}

async function readAssetDataUrl(scriptsDir, name) {
  try {
    const buf = await fs.readFile(path.join(scriptsDir, "assets", name));
    return "data:image/svg+xml;base64," + buf.toString("base64");
  } catch {
    return "";
  }
}

export async function buildWorkDaddyPanelSource({ apiBase = "/workdaddy" } = {}) {
  const home = resolveWorkDaddyHome();
  if (!home) {
    return null;
  }

  const scriptsDir = path.join(home, "scripts");
  const files = {
    inject: path.join(scriptsDir, "inject.js"),
    compat: path.join(scriptsDir, "workbuddy-compat.js"),
    toast: path.join(scriptsDir, "toast-runtime.js"),
    markdown: path.join(scriptsDir, "markdown-preview.js"),
    picker: path.join(scriptsDir, "automation-picker.js"),
    pickerInternal: path.join(scriptsDir, "picker-internal.js"),
  };

  const stamp = [
    await fileMtime(files.inject),
    await fileMtime(files.compat),
    await fileMtime(files.toast),
    await fileMtime(files.markdown),
    await fileMtime(files.picker),
    await fileMtime(files.pickerInternal),
    apiBase,
  ].join(":");

  if (cache.source && cache.key === stamp) {
    return { source: cache.source, etag: cache.etag, home };
  }

  let injectScript;
  let markdownScript;
  let toastScript;
  let compatScript;
  try {
    [injectScript, markdownScript, toastScript, compatScript] = await Promise.all([
      fs.readFile(files.inject, "utf8"),
      fs.readFile(files.markdown, "utf8"),
      fs.readFile(files.toast, "utf8"),
      fs.readFile(files.compat, "utf8"),
    ]);
  } catch {
    return null;
  }

  if (injectScript.includes(PANEL_ANCHOR)) {
    try {
      const picker = await fs.readFile(files.picker, "utf8");
      injectScript = injectScript.replace(PANEL_ANCHOR, picker + "\n" + PANEL_ANCHOR);
    } catch {
      // 没有拾取器不影响面板主体
    }
    if (existsSync(files.pickerInternal)) {
      try {
        const pickerInternal = await fs.readFile(files.pickerInternal, "utf8");
        injectScript = injectScript.replace(PANEL_ANCHOR, pickerInternal + "\n" + PANEL_ANCHOR);
      } catch {
        // 调试模块可选
      }
    }
  }

  const [version, avatarLogo, logo, buddyMark] = await Promise.all([
    readDaemonVersion(),
    readAssetDataUrl(scriptsDir, "workdaddy-app-icon-source.svg"),
    readAssetDataUrl(scriptsDir, "workdaddy-logo.svg"),
    readAssetDataUrl(scriptsDir, "workbuddy-buddy-mark.svg"),
  ]);

  const source = (markdownScript + "\n" + toastScript + "\n" + compatScript + "\n" + injectScript)
    .replace(/__WBS_API__/g, apiBase)
    .replace(/__WBS_VERSION__/g, version || "web")
    .replace(/__WBS_API_TOKEN__/g, "remote-web")
    .replace(/__WBS_DIAGNOSTICS_ENABLED__/g, "false")
    .replace(/__WBS_PROFILE__/g, "workbuddy-cn")
    .replace(/__WBS_CAPS__/g, () => JSON.stringify(PANEL_CAPS))
    .replace(/__WBS_AVATAR_LOGO__/g, avatarLogo)
    .replace(/__WBS_LOGO__/g, logo)
    .replace(/__WBS_BUDDY_MARK__/g, buddyMark)
    .replace(/__WBS_PLATFORM__/g, JSON.stringify(process.platform));

  const etag = `"wbs-panel-${version || "web"}-${stamp.length}-${source.length}"`;
  cache.key = stamp;
  cache.source = source;
  cache.etag = etag;
  return { source, etag, home };
}
