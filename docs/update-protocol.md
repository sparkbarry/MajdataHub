# RhythmHub 更新协议（v1 草案）

本文档定义 RhythmHub 启动器与静态更新源（CDN / 对象存储）之间的约定，以及本地安装目录结构。
设计依据见项目计划书；与计划书的差异在于**所有路径都按 `gameId` 区分**，为多游戏管理预留。

## 1. 标识符

| 名称 | 示例 | 规则 |
|---|---|---|
| `gameId` | `majdataplay`、`ourtaiko` | 小写 ASCII 字母数字、`-`、`_`、`.`；用作目录名 |
| `platform` | `win-x64`、`osx-arm64`、`osx-x64` | .NET RID 风格 |
| `channel` | `stable` | 首版仅 `stable` |
| `version` | `1.2.0`、`1.2.0-beta.1` | 同上字符集；比较时取数字主体 |

客户端会拒绝任何含路径分隔符或 `..` 的标识符（它们来自网络，会被拼进本地路径）。

## 2. 云端目录

```text
{feedBase}/
└── game/
    └── {gameId}/
        └── {platform}/
            ├── {channel}.json                    # 频道清单，短缓存
            └── releases/{version}/game.zip       # 不可变，长缓存
```

- `releases/` 下的文件发布后**永不覆盖**；`Cache-Control: public, max-age=31536000, immutable`。
- `{channel}.json` 使用短缓存或 revalidation；客户端请求时带 `Cache-Control: no-cache`。
- 发布顺序：先上传 zip 并确认可访问，最后替换 `{channel}.json`。
- Launcher 自身的 Velopack feed 放在 `launcher/{platform}/`，布局以 `vpk` 实际输出为准（阶段 C）。

## 3. 频道清单

```json
{
  "schemaVersion": 1,
  "gameId": "majdataplay",
  "channel": "stable",
  "platform": "osx-arm64",
  "version": "1.1.0",
  "minimumLauncherVersion": "0.1.0",
  "releaseNotesUrl": "https://updates.example.com/public/release-notes/majdataplay/1.1.0.md",
  "package": {
    "url": "https://updates.example.com/game/majdataplay/osx-arm64/releases/1.1.0/game.zip",
    "sizeBytes": 428730142,
    "sha256": "<64 位小写 hex>",
    "installedSizeBytes": 912345678
  },
  "entrypoint": "MajdataPlay.app",
  "signature": { "algorithm": "Ed25519", "keyId": "release-key-2026", "value": "<base64>" }
}
```

客户端校验（`ManifestClient.Validate`）：

- `schemaVersion == 1`；`gameId` / `platform` / `channel` 与请求一致。
- `package.url` 必须为 HTTPS（仅回环地址允许 HTTP，供本地开发）。
- `sha256` 为 64 位 hex；`sizeBytes > 0`。
- `entrypoint` 是单个文件名或 bundle 名（Windows 为 `.exe`，macOS 为 `.app`），不含路径。
- `minimumLauncherVersion` 高于当前启动器版本时报 `LauncherTooOld`，不下载。
- **`signature` 在阶段 B 启用**：签名覆盖去掉 `signature` 字段后按 RFC 8785 规范化的 UTF-8 字节；公钥内置于客户端，按 `keyId` 选择。

### 版本判定

频道清单是权威来源：只要清单版本 ≠ 本地 `activeVersion`，即视为“有可用版本”。
因此把清单重新指向旧版即可完成服务端紧急回滚。

## 4. 安装包

- Windows：Unity Player 目录直接打 zip，根目录含 `{Name}.exe`、`UnityPlayer.dll`、`{Name}_Data/`。
- macOS：签名、公证、staple **之后**再打 zip，根目录为 `{Name}.app`。
  zip 必须保留 Unix 权限位与符号链接（`tools/RhythmHub.ReleaseTool pack` 或 `ditto -c -k`）。
- 解压时拒绝：绝对路径、`..` 穿越、指向包外的符号链接、设备/FIFO 等特殊文件、超过
  `installedSizeBytes × 1.1`（未声明时 8 GB）的解压体积。

## 5. 本地目录

```text
{installRoot}/                        # macOS: ~/Library/Application Support/RhythmHub
├── logs/launcher-YYYYMMDD.log        # Windows: %LOCALAPPDATA%\RhythmHub
└── games/
    └── {gameId}/
        ├── versions/
        │   └── {version}/
        │       ├── MajdataPlay.app | MajdataPlay.exe …
        │       └── .rhythmhub/release.json
        ├── downloads/{version}.zip.partial | {version}.zip
        ├── staging/{uuid}/
        ├── current.json
        └── pending.json               # 阶段 B
```

`current.json`：

```json
{
  "schemaVersion": 1,
  "activeVersion": "1.1.0",
  "previousGoodVersion": "1.0.0",
  "channel": "stable",
  "lastLaunchSuccessful": true
}
```

- 写入方式：同目录临时文件 → flush 到磁盘 → rename 覆盖。替换 `current.json` 即为安装提交点。
- 安装流程：下载到 `.partial` → SHA-256 校验 → 改名 `.zip` → 解压到 `staging/{uuid}` →
  检查入口与平台结构 → 移动到 `versions/{version}` → 提交 `current.json` → 删除 zip、清理旧版本。
- 始终保留 `activeVersion` 与 `previousGoodVersion`，其余版本在安装后清理。
- 存档在 Unity `Application.persistentDataPath`，不在 `versions/` 下，启动器不触碰。

## 6. 本地开发

| 环境变量 | 作用 |
|---|---|
| `RHYTHMHUB_FEED_URL` | 覆盖 `games.json` 里的 `feedBaseUrl`，例如 `http://127.0.0.1:8787/` |
| `RHYTHMHUB_INSTALL_ROOT` | 把所有游戏数据和日志放到别处，不影响真实安装 |
