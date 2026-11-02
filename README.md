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
# 1. 生成一个假游戏并打包成 1.0.0
dotnet run --project tools/RhythmHub.ReleaseTool -- fake-game --platform osx-arm64 --name FakeGame --out /tmp/fake-v1 --label v1
dotnet run --project tools/RhythmHub.ReleaseTool -- pack --game majdataplay --platform osx-arm64 --version 1.0.0 \
  --input /tmp/fake-v1 --entrypoint FakeGame.app --base-url http://127.0.0.1:8787/ --out dev-cdn

# 2. 起一个静态服务器
python3 -m http.server 8787 --bind 127.0.0.1 --directory dev-cdn

# 3. 用独立数据目录启动启动器
RHYTHMHUB_FEED_URL=http://127.0.0.1:8787/ RHYTHMHUB_INSTALL_ROOT=/tmp/rhythmhub-dev \
  dotnet run --project src/RhythmHub.Launcher
```

再用 `pack --version 1.1.0` 发布新版，点“检查更新”即可测试升级与“恢复上一版本”。

## 进度

- [x] 阶段 A：多游戏数据模型、清单获取与校验、全量下载、SHA-256、安全解压、版本目录与 `current.json`、启动、基础 UI、日志
- [ ] 阶段 B：Manifest Ed25519 签名、断点续传、`pending.json` 恢复、单实例锁、启动健康标记
- [ ] 阶段 C：Velopack 自更新、CI、代码签名 / 公证、R2 发布
