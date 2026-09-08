# AnMusic 🎵

一款基于 WPF 的桌面音乐播放器，支持本地音乐库管理与 B 站在线音乐搜索、播放与下载。

![.NET](https://img.shields.io/badge/.NET-10.0-blueviolet) ![WPF](https://img.shields.io/badge/UI-WPF-blue) ![NAudio](https://img.shields.io/badge/Audio-NAudio%203.1-green)

## 功能特性

### 音乐播放
- 本地音频播放（mp3 / flac / wav / m4a / aac / wma / ogg），基于 NAudio 3.1
- B 站视频音频搜索、在线播放（自动缓冲到本地）与**下载到本地**（重名自动编号，下载后可直接入库播放）
- 进度条支持**点击跳转**与拖动微调
- 四态播放模式一键切换：**顺序播放 → 列表循环 → 单曲循环 → 随机播放**
- "播放列表"面板：实时预览接下来 5 首待播曲目（按当前模式计算）
- 全局媒体键（播放/暂停、上一首、下一首、停止），后台也可控制
- 音量记忆、窗口位置与尺寸记忆

### 歌单与曲库
- 本地音乐库自动扫描，读取标签元数据与内嵌封面
- 歌单：创建 / 重命名 / 删除 / **批量导入**本地音频文件（自动读取标签与封面，去重）
- 曲目右键菜单：播放、下一首播放、加入/移出我喜欢、下载、添加到歌单、从歌单中删除
- "我喜欢"收藏（本地与 B 站曲目均支持）、最近播放记录
- 搜索历史下拉、按来源筛选（全部 / 本地 / B 站）

### 歌词
- 自动匹配歌词：LRCLIB 在线歌词库 + 本地同名 `.lrc` 文件
- 逐行同步滚动高亮
- **歌词翻译**：必应翻译优先（国内直连），Google 接口自动兜底，原文译文双行对照
- 歌词字号（12–24px）与颜色（跟随主题 / 白 / 黑 / 粉 / 蓝 / 绿）自定义

### 音效
- 10 段图形均衡器（基于 `NAudio.Effects.GraphicEqualizer`）
- 预设音效一键应用，增益状态跨歌曲保留

### 界面
- 深色 / 浅色主题切换（科技蓝强调色）
- 自定义背景图片 + 不透明度调节，设置页/歌词页/内容区统一半透明蒙层
- 无边框圆角窗口（Win11 DWM 原生圆角），自绘标题栏（齿轮设置按钮）
- 全局圆角按钮风格

## 技术栈

| 组件 | 说明 |
|---|---|
| .NET 10 (LTS) | 运行时 |
| WPF + MVVM | UI 框架，CommunityToolkit.Mvvm 源生成器 |
| NAudio 3.1 | 音频引擎与均衡器（`NAudio.Effects`） |
| TagLibSharp | 音频元数据与封面读取 |
| MaterialDesignThemes | 部分控件样式 |
| Microsoft.Extensions.DependencyInjection | 依赖注入 |
| Inno Setup 6 | 安装包制作 |

## 快速开始

### 环境要求
- Windows 10 / 11
- .NET 10 SDK（仅构建时需要；发布的成品自带运行时）

### 从源码运行

```bash
git clone <仓库地址>
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

产物：`publish/AnMusic.exe`（约 75 MB，免安装，可拷贝到任意 Windows 机器运行）

### 制作安装包

1. 安装 [Inno Setup 6](https://jrsoftware.org/isinfo.php)
2. 执行 `dotnet publish`（见上一步）
3. 编译脚本：

```bash
"C:\Program Files (x86)\Inno Setup 6\ISCC.exe" setup.iss
```

产物：`installer/AnMusic-Setup-<版本>.exe`，按用户安装无需管理员权限，卸载时保留用户数据。

## 项目结构

```
AnMusic/
├── setup.iss                     # Inno Setup 安装包脚本
├── installer/                    # 安装包输出目录
├── publish/                      # 发布输出目录
└── src/AnMusic/
    ├── Models/                   # 数据模型（Track、Playlist、Lyric…）
    ├── ViewModels/               # MVVM 视图模型
    │   ├── MainViewModel.cs      # 主视图状态/搜索/歌单管理
    │   ├── PlaybackBarViewModel  # 播放控制/进度/音量/播放模式
    │   ├── LyricViewModel.cs     # 歌词加载/同步/翻译
    │   └── SettingsViewModel.cs  # 主题/背景图/歌词样式设置
    ├── Views/                    # 页面与控件
    │   ├── Pages/                # 设置页等
    │   └── Controls/             # 歌词页 LyricView 等
    ├── Services/
    │   ├── Audio/                # NAudio 引擎封装、均衡器
    │   ├── Providers/            # 本地文件 / B站 API Provider
    │   ├── Playlist/             # 播放队列、用户数据持久化
    │   ├── Lyrics/               # LRCLIB/本地歌词、翻译服务
    │   └── Settings/             # 用户设置
    ├── Converters/               # 值转换器（含 ContextMenu 绑定代理）
    └── Themes/                   # 深色/浅色主题资源字典
```

## 数据存储

| 数据 | 位置 |
|---|---|
| 用户数据（歌单、我喜欢、最近播放、搜索历史） | `%LocalAppData%\AnMusic\userdata.json` |
| 应用设置（主题、背景图、音量、窗口状态等） | `%LocalAppData%\AnMusic\settings.json` |
| 在线音频缓存 / 下载目录 | 设置页可配置，默认 `音乐\AnMusic` |

卸载应用不会删除以上数据。

## 常见问题

- **B 站搜索报 412 / 搜不到？** 已内置浏览器请求头与 Cookie 处理；若仍失败多为 B 站风控临时拦截，稍后重试即可。
- **歌词翻译失败？** 优先走必应翻译（国内可用），失败时自动切 Google；请确认网络可访问 `cn.bing.com` 或 `translate.google.com`。
- **B 站下载的文件没有封面/标签？** B 站音频为 fMP4 流格式，程序已做文件名回退解析（`歌手 - 标题.m4a`）。

## License

仅供学习交流使用。
