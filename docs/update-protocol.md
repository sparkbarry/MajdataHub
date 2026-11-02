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

## 1.1 下载与续传

- 版本 zip 必须支持 HTTP Range，并返回强 `ETag`（或 `Last-Modified`）。
- 续传请求带 `Range: bytes=N-` 与 `If-Range: <ETag>`；服务器返回 206 时追加，返回 200（文件已变化或不支持 Range）时丢弃旧的部分文件从头下载。拼接后的文件仍须通过 SHA-256 校验。
- 网络错误、超时（30 秒无数据）、429/503 自动重试最多 4 次，指数退避（尊重 `Retry-After`）；404/410 不重试。

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
- 签名必须有效（见下节），**签名校验先于其他任何字段被使用**。

### 签名

| 项 | 规定 |
|---|---|
| 算法 | Ed25519（RFC 8032），`signature.algorithm` 必须为 `"Ed25519"` |
| 签名内容 | 收到的 JSON 去掉顶层 `signature` 成员后，按 RFC 8785 (JCS) 规范化得到的 UTF-8 字节 |
| 数字 | 清单中只允许 ±2^53 以内的整数；出现小数、指数或超范围整数时拒绝（避免 JCS 数字格式化分歧） |
| 编码 | `signature.value` 为 64 字节签名的标准 base64；公钥为 32 字节原始公钥的 base64 |
| 信任 | 客户端只信任编译进启动器的 `keyId → 公钥` 表（`ReleaseKeys.cs`）；清单里不携带公钥，未知 `keyId` 直接拒绝 |
| 校验对象 | 对**收到的原始字节**解析后校验，而不是对反序列化后的模型重新序列化，因此清单中任何多余字段也受签名保护 |

密钥轮换：生成新密钥对 → 新启动器版本同时内置新旧公钥 → 等该版本普及后改用新 `keyId` 签名 →
确认无已发布清单使用旧 `keyId` 后，在之后的启动器版本中移除旧公钥。私钥只存放在 CI Secret（`RHYTHMHUB_SIGNING_KEY`）。

Debug 构建可以用环境变量 `RHYTHMHUB_DEV_PUBLIC_KEY=keyId=base64公钥` 额外信任一把本地测试密钥；Release 构建不读取该变量。

### 版本判定

频道清单是权威来源：只要清单版本 ≠ 本地 `activeVersion`，即视为“有可用版本”。
因此把清单重新指向旧版即可完成服务端紧急回滚。

## 4. 安装包

- Windows：Unity Player 目录直接打 zip，根目录含 `{Name}.exe`、`UnityPlayer.dll`、`{Name}_Data/`。
- macOS：签名、公证、staple **之后**再打 zip，根目录为 `{Name}.app`。
  zip 必须保留 Unix 权限位与符号链接（`tools/RhythmHub.ReleaseTool pack` 或 `ditto -c -k`）。
- 解压时拒绝：绝对路径、`..` 穿越、指向包外的符号链接、设备/FIFO 等特殊文件、超过
  `installedSizeBytes × 1.1`（未声明时 8 GB）的解压体积。
- 声明了 `installedSizeBytes` 时，解压后普通文件总字节数必须与之完全相等。

## 5. 本地目录

```text
{installRoot}/                        # macOS: ~/Library/Application Support/RhythmHub
├── launcher.lock                     # 单实例锁；Windows: %LOCALAPPDATA%\RhythmHub
├── logs/launcher-YYYYMMDD.log
└── games/
    └── {gameId}/
        ├── versions/
        │   └── {version}/
        │       ├── MajdataPlay.app | MajdataPlay.exe …
        │       └── .rhythmhub/release.json
        ├── downloads/{version}.zip.partial | {version}.zip
        ├── staging/{uuid}/
        ├── health/health.json         # 游戏写入的启动健康标记
        ├── current.json
        └── pending.json
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
- `previousGoodVersion` 只会是**成功启动过**的版本：安装新版时，若当前版本从未通过启动健康检查，则保留原来的
  `previousGoodVersion` 作为回滚目标（首个安装的版本例外）。回滚后被放弃的版本不再作为回滚目标。

`pending.json` 记录进行中的更新，**从不作为已安装的依据**：

```json
{
  "schemaVersion": 1,
  "version": "1.1.0",
  "sha256": "…",
  "packageUrl": "https://…/game.zip",
  "sizeBytes": 428730142,
  "stage": "Downloading | Downloaded | Installing",
  "etag": "\"abc\"",
  "lastModified": "Sun, 01 Nov 2026 10:00:00 GMT",
  "updatedAt": "2026-11-01T10:00:00Z"
}
```

启动时恢复（`GameUpdateService.RecoverAsync`），在持有单实例锁后执行：

| 情况 | 处理 |
|---|---|
| `Installing` 且 `current.json` 已指向该版本 | 提交已完成，只差清理：删除 zip 与 `pending.json` |
| `Installing` / `Downloaded` 且已校验的 zip 还在 | 回到 `Downloaded`，下次直接安装、不重新下载 |
| `.partial` 还在 | 回到 `Downloading`，下次带 `If-Range` 续传 |
| 其余 | 删除 `pending.json` |

之后清空 `staging/`、删除 `current.json` 未引用的版本目录，以及 `downloads/` 中 `pending.json` 未记录的文件。
清单指向别的版本或换了 hash 时，旧的部分下载会被丢弃。
- 存档在 Unity `Application.persistentDataPath`，不在 `versions/` 下，启动器不触碰。

## 6. 启动健康检查

启动游戏时，启动器生成一次性 nonce，并通过环境变量传给游戏进程（macOS 用 `open -n --env`）：

| 变量 | 内容 |
|---|---|
| `RHYTHMHUB_HEALTH_FILE` | 健康标记文件的绝对路径 |
| `RHYTHMHUB_LAUNCH_NONCE` | 本次启动的 32 位 hex nonce |
| `RHYTHMHUB_GAME_VERSION` | 启动器认为正在启动的版本号 |

游戏完成初始化（例如进入标题画面）后，写临时文件再 rename 到 `RHYTHMHUB_HEALTH_FILE`：

```json
{ "nonce": "<RHYTHMHUB_LAUNCH_NONCE>", "version": "<RHYTHMHUB_GAME_VERSION>" }
```

可直接使用 [`docs/unity/RhythmHubHealth.cs`](unity/RhythmHubHealth.cs)，在合适时机调用 `RhythmHubHealth.ReportReady()`。

启动器判定：

| 结果 | 条件 | 处理 |
|---|---|---|
| Healthy | 读到 nonce 和版本都匹配的标记 | `lastLaunchSuccessful = true`，该版本此后可作为回滚目标 |
| Exited | 未报告就绪而进程已消失（5 秒启动宽限期后） | `lastLaunchSuccessful = false`，界面提示可“恢复上一版本” |
| TimedOut | 2 分钟内仍在运行但没有标记 | 保持原状态，提示用户若异常可回滚 |

尚未接入标记的游戏在 `games.json` 中设 `"healthCheck": false`：进程活过宽限期即视为 Healthy（较弱的判定）。
接入后改为 `true`。启动器不会自动回滚，只提示，由用户决定。

## 7. 本地开发

| 环境变量 | 作用 |
|---|---|
| `RHYTHMHUB_FEED_URL` | 覆盖 `games.json` 里的 `feedBaseUrl`，例如 `http://127.0.0.1:8787/` |
| `RHYTHMHUB_INSTALL_ROOT` | 把所有游戏数据和日志放到别处，不影响真实安装 |
| `RHYTHMHUB_DEV_PUBLIC_KEY` | 仅 Debug 构建：`keyId=base64公钥`，额外信任的本地测试签名公钥 |
| `RHYTHMHUB_SIGNING_KEY` | 发布工具使用的 base64 私钥（只放在本机测试或 CI Secret 中） |
