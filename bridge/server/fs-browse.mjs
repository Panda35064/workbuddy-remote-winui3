import fs from "node:fs/promises";
import os from "node:os";
import path from "node:path";

/**
 * 服务端目录浏览 API，供网页端“选择文件夹”对话框使用。
 * 只暴露读目录 / 建目录两类操作，路径必须是绝对路径（不接受相对路径越界解释）。
 */

const MAX_ENTRIES = 3000;

function normalizeDir(input) {
  let value = String(input || "").trim();
  if (!value) return "";
  value = value.replace(/\//g, path.sep);
  if (!path.isAbsolute(value)) {
    throw new Error("需要绝对路径");
  }
  return path.normalize(value);
}

function driveRoots() {
  if (process.platform !== "win32") {
    return [{ path: "/", label: "/", kind: "drive" }];
  }
  const roots = [];
  for (let code = 65; code <= 90; code += 1) {
    roots.push({ path: String.fromCharCode(code) + ":\\", label: "本地磁盘 (" + String.fromCharCode(code) + ":)" });
  }
  return roots;
}

async function pathExists(target) {
  try {
    await fs.access(target);
    return true;
  } catch {
    return false;
  }
}

export async function listRoots() {
  const roots = [];
  const home = os.homedir();
  const specials = [
    { path: path.join(home, "Desktop"), label: "桌面" },
    { path: path.join(home, "Documents"), label: "文档" },
    { path: path.join(home, "Downloads"), label: "下载" },
    { path: home, label: "用户目录" },
  ];

  roots.push({ path: home, label: path.basename(home) || home, kind: "home" });

  if (process.platform === "win32") {
    for (const drive of driveRoots()) {
      if (await pathExists(drive.path)) {
        roots.push({ path: drive.path, label: drive.label, kind: "drive" });
      }
    }
  } else {
    roots.push({ path: "/", label: "/", kind: "drive" });
  }

  for (const item of specials) {
    if (item.path !== home && (await pathExists(item.path))) {
      roots.push({ path: item.path, label: item.label, kind: "special" });
    }
  }

  return roots;
}

function describeType(entry) {
  if (entry.dir) return "文件夹";
  const ext = path.extname(entry.name).toLowerCase();
  const map = {
    ".png": "PNG 文件",
    ".jpg": "JPG 文件",
    ".jpeg": "JPEG 文件",
    ".gif": "GIF 文件",
    ".webp": "WEBP 文件",
    ".svg": "SVG 文件",
    ".pdf": "PDF 文件",
    ".txt": "文本文档",
    ".md": "Markdown 文件",
    ".json": "JSON 文件",
    ".js": "JavaScript 文件",
    ".mjs": "JavaScript 文件",
    ".ts": "TypeScript 文件",
    ".zip": "压缩文件",
    ".rar": "压缩文件",
    ".7z": "压缩文件",
    ".exe": "应用程序",
    ".msi": "安装程序",
    ".mp4": "视频文件",
    ".mkv": "视频文件",
    ".mp3": "音频文件",
    ".wav": "音频文件",
    ".xlsx": "Excel 工作表",
    ".xls": "Excel 工作表",
    ".docx": "Word 文档",
    ".doc": "Word 文档",
    ".pptx": "PowerPoint 演示文稿",
    ".ppt": "PowerPoint 演示文稿",
  };
  return map[ext] || (ext ? ext.slice(1).toUpperCase() + " 文件" : "文件");
}

export async function listDirectory(inputPath) {
  const target = normalizeDir(inputPath);
  if (!target) {
    throw new Error("缺少路径");
  }

  const dirEntries = await fs.readdir(target, { withFileTypes: true });
  const limited = dirEntries.slice(0, MAX_ENTRIES);
  const entries = [];

  for (const dirent of limited) {
    const fullPath = path.join(target, dirent.name);
    let size = 0;
    let mtime = 0;
    try {
      const info = await fs.stat(fullPath);
      size = dirent.isDirectory() ? 0 : info.size;
      mtime = info.mtimeMs;
    } catch {
      // 权限不足的条目仍然显示，只是没有大小/时间
    }
    entries.push({
      name: dirent.name,
      path: fullPath,
      dir: dirent.isDirectory(),
      size,
      mtime,
      type: describeType({ name: dirent.name, dir: dirent.isDirectory() }),
    });
  }

  entries.sort((a, b) => {
    if (a.dir !== b.dir) return a.dir ? -1 : 1;
    return a.name.localeCompare(b.name, "zh-Hans-CN");
  });

  const parent = path.dirname(target);
  return {
    path: target,
    parent: parent && parent !== target ? parent : "",
    entries,
    truncated: dirEntries.length > MAX_ENTRIES,
  };
}

export async function makeDirectory(parentPath, name) {
  const parent = normalizeDir(parentPath);
  const folderName = String(name || "").trim();
  if (!parent) throw new Error("缺少父目录");
  if (!folderName) throw new Error("请输入文件夹名称");
  if (/[\\/:*?"<>|]/u.test(folderName)) throw new Error("文件夹名称包含非法字符");

  const target = path.join(parent, folderName);
  await fs.mkdir(target);
  return target;
}
