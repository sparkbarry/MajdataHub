# RhythmHub

Windows / macOS 桌面启动器，负责节奏游戏（MajdataPlay、OurTaiko 等 Unity 游戏）的安装、更新、校验、启动与回滚。
游戏本体在各自仓库中构建；本仓库只包含启动器和发布工具。

## 结构

```text
src/
  RhythmHub.Launcher/   Avalonia UI（MVVM），组合根，games.json 游戏目录
  RhythmHub.Updater/    平台无关更新核心：清单、下载、SHA-256、安全解压、事务化安装、回滚
  RhythmHub.Platform/   Windows / macOS 差异：默认目录、启动、进程检测、包结构检查
tests/
  RhythmHub.Updater.Tests/
tools/
  RhythmHub.ReleaseTool/  打包 Unity 构建、生成频道清单；生成本地测试用假游戏
docs/
  update-protocol.md    更新协议与本地目录约定
```

## 开发

需要 .NET 10 SDK。

```bash
dotnet build
dotnet test
dotnet run --project src/RhythmHub.Launcher
```

### 本地假 CDN 跑通完整流程

```bash
T="dotnet run --project tools/RhythmHub.ReleaseTool --"

# 1. 生成本地测试签名密钥（只用于本机）
$T keygen --out /tmp/rhythmhub-dev.key          # 记下输出的 public key
export RHYTHMHUB_SIGNING_KEY=$(cat /tmp/rhythmhub-dev.key)

# 2. 生成一个假游戏并打包、签名为 1.0.0
$T fake-game --platform osx-arm64 --name FakeGame --out /tmp/fake-v1 --label v1
$T pack --game majdataplay --platform osx-arm64 --version 1.0.0 --key-id dev-local \
  --input /tmp/fake-v1 --entrypoint FakeGame.app --base-url http://127.0.0.1:8787/ --out dev-cdn

# 3. 起一个静态服务器
python3 -m http.server 8787 --bind 127.0.0.1 --directory dev-cdn

# 4. 用独立数据目录和测试公钥启动（Debug 构建）
RHYTHMHUB_FEED_URL=http://127.0.0.1:8787/ RHYTHMHUB_INSTALL_ROOT=/tmp/rhythmhub-dev \
RHYTHMHUB_DEV_PUBLIC_KEY="dev-local=<上面的 public key>" \
  dotnet run --project src/RhythmHub.Launcher
```

再用 `pack --version 1.1.0` 发布新版测试升级；`fake-game --crash true` 生成一个启动即崩溃的版本，可测试“恢复上一版本”。
假游戏会写健康标记，测试时可把 `games.json` 里对应游戏的 `healthCheck` 改为 `true`。

### 发布签名密钥

1. `keygen` 生成正式密钥对，私钥存为 CI Secret `RHYTHMHUB_SIGNING_KEY`，**不要提交到仓库**。
2. 把公钥填进 [ReleaseKeys.cs](src/RhythmHub.Launcher/Services/ReleaseKeys.cs)。未配置任何公钥的 Release 构建会拒绝所有清单。

### 游戏接入健康检查

把 [docs/unity/RhythmHubHealth.cs](docs/unity/RhythmHubHealth.cs) 放进游戏工程，在进入标题画面时调用
`RhythmHubHealth.ReportReady()`，然后把 `games.json` 中该游戏的 `healthCheck` 改为 `true`。

## 进度

- [x] 阶段 A：多游戏数据模型、清单获取与校验、全量下载、SHA-256、安全解压、版本目录与 `current.json`、启动、基础 UI、日志
- [x] 阶段 B：Manifest Ed25519 签名（RFC 8785）、Range/If-Range 续传与重试、`pending.json` 中断恢复、安装大小校验、单实例锁、启动健康标记与回滚提示
- [ ] 阶段 C：Velopack 自更新、CI、代码签名 / 公证、R2 发布
