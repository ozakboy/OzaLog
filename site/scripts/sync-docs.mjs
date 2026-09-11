#!/usr/bin/env node
// 將 repo 根目錄的 docs/ 同步到 site/content/,讓 @nuxt/content 讀取。
// Sync repo-root docs/ to site/content/ so @nuxt/content can read them.
//
// 觸發時機 / Triggered by:
//   - npm run dev      (predev hook)
//   - npm run generate (pregenerate hook)
//   - npm run build    (prebuild hook)
//   - npm run sync-docs (manual)
//
// 注意: 不用 fs.cpSync —— Node 22.22.x 在 Windows + 含 CJK 字元的路徑下會
// 觸發 STATUS_ACCESS_VIOLATION (0xC0000005) 直接 crash process。
// NOTE: We avoid fs.cpSync because Node 22.22.x crashes (STATUS_ACCESS_VIOLATION)
// when copying recursively across paths containing CJK characters on Windows.
// See https://github.com/nodejs/node/issues (search "cpSync access violation").

import fs from 'node:fs'
import path from 'node:path'
import { fileURLToPath } from 'node:url'

const __dirname = path.dirname(fileURLToPath(import.meta.url))
const SITE_DIR = path.resolve(__dirname, '..')
const REPO_ROOT = path.resolve(SITE_DIR, '..')
const SRC = path.join(REPO_ROOT, 'docs')
const DST = path.join(SITE_DIR, 'content')

if (!fs.existsSync(SRC)) {
  console.error(`[sync-docs] source not found: ${SRC}`)
  process.exit(1)
}

// 1. Wipe target so deletions in docs/ propagate.
fs.rmSync(DST, { recursive: true, force: true })

// 2a. 改寫 md 內部相對連結為站內絕對路由。
//     docs/ 裡的 `./xxx.md` 在 GitHub 上正確,但在站上會被瀏覽器以「目前網址+尾斜線」
//     解析(例:/OzaLog/docs/api/ + ./configuration.md → /OzaLog/docs/api/configuration → 404),
//     且 prerender crawler 用無尾斜線網址解析、剛好繞過偵測。
//     一律於同步時改寫為絕對路由,依 site/pages/ 實際路由對應:
//       - changelog / migration / benchmarks → 根層路由 /xxx
//       - 其餘(api / configuration / getting-started / async-pipeline)→ /docs/xxx
//     zh-TW(預設語系)無前綴、其他語系加 /<locale>。
//     另:站上 heading id 以數字開頭時會被加 `_` 前綴(HTML id 規則),錨點一併補齊。
//     GitHub 上的 docs/ 原始檔完全不受影響。
// 2a. Rewrite relative md links to absolute site routes at sync time.
//     `./xxx.md` works on GitHub but breaks on the static site (trailing-slash URL
//     resolution), and the prerender crawler resolves slash-less URLs so it never
//     catches it. Route mapping follows site/pages/: changelog / migration /
//     benchmarks live at root (/xxx), everything else under /docs/xxx.
//     zh-TW (default locale) has no prefix; other locales get /<locale>.
//     Heading ids starting with a digit get a `_` prefix on the site; fix anchors too.
const ROOT_PAGES = new Set(['changelog', 'migration', 'benchmarks'])

function rewriteDocLinks(markdown, locale) {
  const localePrefix = locale === 'zh-TW' ? '' : `/${locale}`
  return markdown.replace(
    /\]\(\.\/([A-Za-z0-9_-]+)\.md(#[^)]*)?\)/g,
    (_m, name, anchor) => {
      let frag = anchor || ''
      if (/^#\d/.test(frag)) frag = `#_${frag.slice(1)}`
      const base = ROOT_PAGES.has(name) ? `/${name}` : `/docs/${name}`
      return `](${localePrefix}${base}${frag})`
    },
  )
}

// 2b. Manual recursive copy (avoids fs.cpSync Windows + CJK bug).
let count = 0
function copyDir(srcDir, dstDir, locale) {
  fs.mkdirSync(dstDir, { recursive: true })
  for (const entry of fs.readdirSync(srcDir, { withFileTypes: true })) {
    const srcPath = path.join(srcDir, entry.name)
    const dstPath = path.join(dstDir, entry.name)
    if (entry.isDirectory()) {
      // 第一層目錄名即語系(en / zh-TW),往下傳給連結改寫器
      copyDir(srcPath, dstPath, locale ?? entry.name)
    } else if (entry.isFile()) {
      if (entry.name.endsWith('.md') && locale) {
        const text = fs.readFileSync(srcPath, 'utf8')
        fs.writeFileSync(dstPath, rewriteDocLinks(text, locale))
      } else {
        fs.copyFileSync(srcPath, dstPath)
      }
      count++
    }
    // 忽略 symlinks / 其他類型(本專案 docs/ 內只有 .md 檔)
  }
}

copyDir(SRC, DST)

console.log(
  `[sync-docs] ${count} files copied: ` +
  `${path.relative(REPO_ROOT, SRC)} -> ${path.relative(REPO_ROOT, DST)}`
)
