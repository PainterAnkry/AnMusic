# AnMusic 🎵

一款基于 WPF 的桌面音乐播放器：本地曲库 + 网易云 / QQ音乐 / B站在线搜索播放，
支持 MusicFree 兼容 `.js` 音源插件扩展。当前版本 **v3.3.6**。

![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet) ![WPF](https://img.shields.io/badge/UI-WPF-blue) ![NAudio](https://img.shields.io/badge/Audio-NAudio%203.1-green) ![version](https://img.shields.io/badge/version-3.3.6-orange)

## 功能特性

### 音乐播放
- 🏠 **主页**：卡片式落地页——横幅入口（本地歌曲/个性电台/排行榜/听歌排行/最近播放/我喜欢，无封面时用内置设计封面）、
  我的歌单（自定义封面，默认取最近添加的歌曲）、**最近在听**（点封面直接播），全部由现有功能聚合而来
- 本地音频播放（mp3 / flac / wav / m4a / aac / wma / ogg），基于 NAudio 3.1
- **多音源在线搜索**：网易云、QQ音乐、B站（插件源可继续扩展），自动缓冲到本地后播放
- 搜索结果分页展示：每批 **50 条**，点击**「加载更多」**继续翻页，跨页自动去重
- **音源插件（MusicFree 兼容）**：把 `.js` 插件放进插件目录即接入（内置 axios、crypto-js、dayjs、jsencrypt 等宿主库，支持插件清单自动下载）
- 排行榜（热歌/新歌等榜单）、个性电台（按“我喜欢”自动推荐续播）、听歌排行
- 进度条**点击即精确跳转**（点哪播哪、线性映射无偏差），按住可拖动微调
- 四态播放模式：**顺序 → 列表循环 → 单曲循环 → 随机**
- “播放列表”面板实时预览接下来 5 首；**在线一起听**（创建房间/邀请链接加入，同步播放）
- 在线歌曲**下载到本地**（重名自动编号）；全局媒体键；音量/窗口状态记忆

### 歌词
- **歌词页（播放器布局）**：左侧黑胶唱片（专辑封面作中心标签，**播放时转动、暂停即停住**）+ 右侧曲目信息、
  快捷操作（收藏/下载/桌面歌词/翻译）与左对齐歌词；切歌（含自动切歌/上一首下一首）歌词同步更换
- 自动匹配：本地同名 `.lrc` + LRCLIB 在线歌词库
- **手动歌词搜索**：输入歌名（支持 `歌名 - 歌手`）在线匹配，命中当前播放曲目即同步装载，其他歌曲以预览展示
- **桌面歌词**：独立置顶悬浮窗（可拖动/置顶/隐藏）；歌词卡片播放模式
- 逐行同步高亮；**歌词翻译**（译文对照）；字号/颜色自定义

### 用户与歌单
- 用户中心：**圆形头像自由裁剪上传**、昵称编辑、听歌等级/经验成长
- 歌单：创建/重命名/删除/批量导入；“我喜欢”、最近播放、搜索历史
- 曲目右键菜单：播放、下一首播放、收藏、下载、加入歌单等
- 📬 **标题栏「私信」**：应用内通知中心——有新版本时亮红点，点开可直接「下载并安装」；
  也会展示开发者公告（改一个 JSON 就能发，见下「给用户发公告」）

### 音效与界面
- 10 段图形均衡器 + 预设
- 深/浅主题 + 强调色方案、动态壁纸粒子、自定义背景图与透明度
- 无边框圆角窗口（Win11 DWM 原生圆角）、自绘标题栏
- 设置页内置**检查更新**（对比 GitHub Releases）

## 技术栈

| 组件 | 说明 |
|---|---|
| .NET 10 | 运行时 |
| WPF + MVVM | UI 框架，CommunityToolkit.Mvvm 源生成器 |
| NAudio 3.1 | 音频引擎与均衡器 |
| Jint 4 | JS 音源插件宿主（MusicFree 兼容子集） |
| TagLibSharp | 音频元数据与封面读取 |
| Inno Setup 6 | 安装包制作 |

## 快速开始

### 环境要求
- Windows 10 / 11
- .NET 10 SDK（仅构建时需要；发布的成品自带运行时）

### 从源码运行

```bash
git clone https://github.com/PainterAnkry/AnMusic.git
cd AnMusic
dotnet run --project src/AnMusic/AnMusic.csproj
```

### 构建发布版（自包含单文件）

```bash
dotnet publish src/AnMusic/AnMusic.csproj -c Release -r win-x64 \
  --self-contained true \
  -p:PublishSingleFile=true \
  -p:IncludeNativeLibrariesForSelfExtract=true \
  -p:EnableCompressionInSingleFile=true \
  -o publish
```

产物：`publish/AnMusic.exe`（约 80 MB，免安装，可直接运行，便携版即此文件）

### 制作安装包

1. 安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)
2. 执行 `dotnet publish`（见上一步）
3. 编译脚本：

```bash
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss
```

产物：`installer/AnMusic-Setup-<版本>.exe`，按用户安装无需管理员权限，卸载时保留用户数据。

## 音源插件（MusicFree 兼容）

1. 打开设置 → 音源插件 →「📂 插件文件夹」，把社区 `.js` 音源插件放入
2. 点击「🔄 重新加载」即可在搜索来源中看到新音源
3. 也可以在 `plugins.json` 清单里声明远程插件地址，应用启动时自动下载

插件需实现 MusicFree 协议函数：`search(keyword, page, type)`、`getMediaSource(musicItem, quality)` 等。
引擎为插件提供了 `require('axios')`、`crypto-js`、`dayjs`、`big-integer`、`jsencrypt`、`he`、`qs` 等宿主模块。

## 给用户发公告（应用内「私信」）

想主动给用户发一条消息（接口变更说明、重要提醒、催老版本升级…），**不需要发新版本**：

1. 编辑仓库根目录的 [`announcements.json`](announcements.json)，往 `messages` 数组里加一条；
2. 提交并推送（网页上直接改也行）；
3. 用户下次启动，或点私信里的「检查更新」，就能看到——未读时标题栏按钮上会有小红点。

```json
{
  "messages": [
    {
      "id": "2026-09-12-plugin-change",
      "title": "在线音源接口变更说明",
      "body": "最近部分音源接口有调整，若搜索无结果请到 设置 → 音源插件 点「重新加载」。\n仍有问题可在 GitHub 反馈。",
      "level": "important",
      "date": "2026-09-12",
      "minVersion": "3.3.0",
      "url": "https://github.com/PainterAnkry/AnMusic/issues",
      "urlText": "去反馈",
      "pinned": true
    }
  ]
}
```

| 字段 | 说明 |
|---|---|
| `id` | **建议必填**：稳定标识，用来记住「已读」。改文案别改 `id`，否则用户会再看到一次未读红点 |
| `title` / `body` | 标题与正文（至少填一个）。正文支持 `\n` 换行与简单 Markdown（`#`/`-`/`**加粗**` 会自动转成纯文本） |
| `level` | `info`（默认 📢）/ `important`（⚠️ 强调色描边）/ `update`（🚀）/ `welcome`（👋） |
| `date` | 右上角日期文案，原样显示 |
| `minVersion` | 只发给**不低于**该版本的用户（留空 = 不限） |
| `maxVersion` | 只发给**不高于**该版本的用户（留空 = 不限，催老版本升级就用它） |
| `url` / `urlText` | 详情按钮的链接与文案（`url` 留空则不显示按钮） |
| `pinned` | 置顶（重要公告建议置顶） |

## 项目结构

```
AnMusic/
├── setup.iss                     # Inno Setup 安装包脚本
├── announcements.json            # 应用内公告（标题栏「私信」的内容源，改完提交即可）
├── installer/                    # 安装包输出目录
├── publish/                      # 发布输出目录
├── src/
│   ├── AnMusic.Core/             # 跨端共享库（net10.0，无平台依赖）
│   │   ├── Models/               # 数据模型（Track、Playlist、Lyric…）
│   │   ├── Services/
│   │   │   ├── AppPaths.cs       # 数据路径统一入口（数据根可被各端覆盖）
│   │   │   ├── IPlatformContext.cs # 平台差异抽象（数据根/权限/日志）
│   │   │   ├── Audio/            # IAudioEngine 抽象（各端各自实现）
│   │   │   ├── Providers/        # B站/网易云/QQ/插件(Jint) 音源
│   │   │   ├── Playlist/         # 播放队列、用户数据持久化
│   │   │   ├── Lyrics/           # LRCLIB/本地歌词
│   │   │   └── Net/              # 统一 HTTP 出口
│   │   └── Assets/Plugins/       # 插件宿主内置 JS 库
│   ├── AnMusic/                  # 桌面端（WPF，net10.0-windows）
│   │   ├── ViewModels/           # MVVM 视图模型
│   │   ├── Views/                # 页面与控件
│   │   ├── Services/Audio/       # NAudio 引擎、均衡器
│   │   └── Themes/               # 皮肤
│   └── AnMusic.Android/          # 安卓端（.NET MAUI，net10.0-android）
│       ├── ViewModels/           # 曲库/播放/搜索/设置/下载/排行/一起听
│       ├── Services/             # MediaPlayer 引擎、后台播放服务、媒体库扫描、主题、平台上下文
│       ├── Resources/Styles/     # Colors.xaml + Styles.xaml（Am* 设计令牌）
│       └── Platforms/Android/    # MainActivity、AndroidManifest、FileProvider 配置
└── tests/
```

### 跨端架构

业务逻辑集中在 **AnMusic.Core**（音源解析、歌词匹配、播放队列、用户数据、设置、听歌统计），
桌面端与安卓端各自只实现两部分：

| 关注点 | 桌面端 | 安卓端 |
|---|---|---|
| UI 框架 | WPF + XAML 皮肤 | .NET MAUI |
| 音频引擎 | NAudio 3.1（`NAudioEngine`） | 系统 MediaPlayer（`AndroidAudioEngine`） |
| 后台播放 | 播放器常驻进程 | 前台服务 + MediaSession + 通知栏（`PlaybackService`） |
| 数据根目录 | `%AppData%\AnMusic` | 应用私有目录 `files/AnMusic` |
| 本地音乐发现 | 递归扫描音乐文件夹 | MediaStore 查询 + 目录枚举 + TagLib 内嵌封面提取 |
| 平台适配 | `DesktopPlatformContext` | `AndroidPlatformContext` |
| 主题 | MaterialDesign 皮肤 | `ThemeService`（7 皮肤 × 6 强调色，DynamicResource 运行时切换） |

两端共用同一份 `userdata.json` / `settings.json` 结构，数据可互相迁移。

**改动时要注意的三条约束**（都是踩过坑的）：

1. `AppPaths` 的子路径、各服务的 `CacheDir` / `FilePath` 必须是**延迟求值属性**，
   不能是 `static readonly` 字段 —— 安卓端要到 `MauiProgram` 才切换数据根。
2. 主题色只能用 `{DynamicResource}`，用 `{StaticResource}` 会导致运行时换肤不生效。
3. 安卓端命名空间是 `AnMusic.Android.*`，引用 `Android.*` 时必须写 `global::Android.*`。

### 构建安卓端

需要 JDK 17、Android SDK（API 36）与 `maui-android` 工作负载：

```bash
# 一键构建（自动加载环境变量、按 csproj 版本号命名产物）
./build-android.sh sign      # 构建 + 用项目密钥库签名到 installer/
./build-android.sh install   # 构建并用 adb 安装到设备
```

签名用到 `AndroidTools/anmusic.keystore`（可用 `ANMUSIC_KEYSTORE` /
`ANMUSIC_KEY_PASS` 覆盖）。注意 `dotnet build` 自带的 `*-Signed.apk` 用的是
**Android 调试密钥**，不能用于发布。

> Release 配置需要 `Microsoft.NETCore.App.Runtime.Mono.win-x64` 10.0.11，
> 该包只随 MSI 版本的工作负载分发、不在 nuget.org 上。若用轻量工作负载安装方式，
> 请用 Debug 配置 + `./build-android.sh sign` 重签。

## 数据存储

| 数据 | 桌面端位置 |
|---|---|
| 用户数据（歌单、我喜欢、最近播放、搜索历史） | `%AppData%\AnMusic\userdata.json` |
| 应用设置（主题、背景图、音量、窗口状态等） | `%AppData%\AnMusic\settings.json` |
| 音源插件目录 / 封面与音频缓存 | `%AppData%\AnMusic\plugins`、`%AppData%\AnMusic\covers` |

安卓端对应数据位于应用私有目录 `/data/data/com.painterankry.anmusic/files/AnMusic/`，卸载即清除。

卸载应用不会删除以上数据。

## 常见问题

- **在线源搜索/播放失败？** 在线能力依赖第三方公开接口与插件网关，偶发风控或超时请稍后重试；插件源加载失败可在设置 → 音源插件 → 重新加载并查看 `plugins/load-errors.log`。
- **歌词翻译失败？** 自动在必应/Google 接口间兜底，请确认网络可访问。
- **B 站音频下载后没有封面/标签？** B 站音频为 fMP4 流格式，程序已做文件名回退解析（`歌手 - 标题.m4a`）。

## License

仅供学习交流使用。
