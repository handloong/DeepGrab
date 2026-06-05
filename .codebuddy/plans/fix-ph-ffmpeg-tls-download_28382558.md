---
name: fix-ph-ffmpeg-tls-download
overview: 将 PH 下载从 ffmpeg 远程拉取改为 HttpClient 分片下载 + ffmpeg 本地拼接，解决 CDN TLS 握手失败问题
todos:
  - id: rewrite-ffmpeg-download
    content: 重写 VideoDownloadService.DownloadWithFfmpegAsync：HttpClient 下载 m3u8 + 解析 ts 列表 + 并发下载 ts 分片 + 生成本地 m3u8 + ffmpeg 本地拼接 + 清理临时文件
    status: completed
  - id: update-main-viewmodel
    content: 更新 MainViewModel.ProcessTaskAsync：传入 _http 并适配新的进度回调签名
    status: completed
    dependencies:
      - rewrite-ffmpeg-download
  - id: build-test
    content: 构建验证，确保 0 错误
    status: completed
    dependencies:
      - update-main-viewmodel
---

## 问题

Pornhub 下载时 ffmpeg 连接 PH CDN (`ev-h.phncdn.com`) TLS 握手失败（WSAECONNRESET -10054）。Python 脚本 `ph.py` 同样失败。

## 根本原因

1. **TLS 层**：PH CDN 拒绝 ffmpeg 内置 SSL 库的连接，但 .NET `HttpClient`（Windows Schannel TLS）和浏览器都可以
2. **Headers 层**：参考 `Pornhub-Video-Downloader-Plugin-v3` 浏览器插件，成功下载的关键是 **Origin + Referer（完整视频页URL，非仅首页）** 两个头，插件通过 `chrome.declarativeNetRequest` 注入，10 并发 fetch ts 分片

## 解决方案

参照插件逻辑：`HttpClient` + 正确 Headers 下载 m3u8 + ts 分片 → 本地 ffmpeg 拼接。

1. **HttpClient** 带 `Origin` + `Referer`（视频页URL）下载 m3u8 内容
2. **解析 m3u8** 获取所有 .ts 分片 URL，基于 m3u8 URL 解析相对路径
3. **HttpClient 6并发** 下载 .ts 分片到临时目录（插件用10并发，我们保守用6）
4. **生成本地 m3u8**，使用 `file 'segment_xxxx.ts'` concat 格式
5. **ffmpeg -f concat -safe 0** 纯本地拼接（不连网，0 TLS 风险）
6. **清理** 临时 ts 文件和 m3u8

## 技术栈

- .NET 8 + C#（现有项目）
- `HttpClient`（MainViewModel 已有实例，30s 超时，支持 cookie 和自动重定向）
- ffmpeg.exe（已通过 `YoutubeDLSharp.Utils.DownloadFFmpeg` 下载到 app 目录）
- m3u8 纯文本解析（无需第三方库，PH 的 m3u8 格式简单）

## 实现方式

### 1. 重写 `DownloadWithFfmpegAsync`

**文件**: `DeepGrab/Services/VideoDownloadService.cs`

新增签名（参考插件：需要 `HttpClient` + `refererUrl` 构造正确的 Origin/Referer）：

```
public async Task<string?> DownloadWithFfmpegAsync(
    string m3u8Url, string outputTitle, string refererUrl, HttpClient http,
    Action<int, int>? onProgress, CancellationToken ct = default)
```

- `http`：传入 MainViewModel 的 `_http` 实例（Schannel TLS）
- `refererUrl`：视频页面 URL，用于构造 Origin + Referer 头（插件也是用视频页而非首页）
- `onProgress(int done, int total)`：每完成一个 ts 回调

**流程**（对标插件的 fetch + 10并发，我们用 HttpClient + 6并发）:

1. 创建临时目录 `_ts_xxxxx/`，构造带 `Origin` + `Referer` 头的 `HttpRequestMessage`
2. `http.SendAsync()` 下载 m3u8 文本（GetStringAsync 无法自定义头）
3. 逐行解析：`#EXTINF:` → 取下一行 ts 文件名，相对于 m3u8 URL 的 base path 拼接完整 URL
4. 用 `SemaphoreSlim(6)` 并发下载 ts，每个 ts 文件都带 `Origin` + `Referer` 头
5. 每完成一个 ts 触发 `onProgress(done, total)`
6. 生成本地 `concat_list.txt`（ffmpeg concat 格式：`file 'segment_0001.ts'`）
7. `ffmpeg -y -f concat -safe 0 -i concat_list.txt -c copy output.mp4`（纯本地，不联网）
8. ffmpeg 退出后清理临时目录
9. 返回 output.mp4 路径 或 null

### 2. 更新 `MainViewModel.ProcessTaskAsync`

**文件**: `DeepGrab/ViewModels/MainViewModel.cs`

传入 `_http` + `referer`：

```
path = await _engine.DownloadWithFfmpegAsync(
    videoUrl, task.Title, referer, _http,
    (done, total) =>
    {
        task.Progress = total > 0 ? (double)done / total * 100 : 0;
        task.Speed = $"ts {done}/{total}";
        task.Eta = "下载中";
    },
    cts.Token);
```

### 3. 关键：下载 ts 时必须带的 Headers

```
Origin:  https://cn.pornhub.com  （从 refererUrl 解析 scheme+host）
Referer: https://cn.pornhub.com/view_video.php?viewkey=xxxxx  （完整视频页 URL）
User-Agent: Mozilla/5.0 ... Chrome/124 ...
```

## 架构设计

```
MainViewModel._http (HttpClient, Schannel TLS, 可连 PH CDN)
    │
    ├── PHSite.ResolveVideoAsync    → 解析出子 m3u8 URL
    │
    └── VideoDownloadService.DownloadWithFfmpegAsync(http, m3u8Url)
            │
            ├── http.GetStringAsync(m3u8Url)        → 下载 m3u8 文本
            ├── 解析 .ts 分片 URL 列表
            ├── http.GetByteArrayAsync(tsUrl) × N    → 并发下载 ts
            ├── File.WriteAllText("local.m3u8")      → 生成本地播放列表
            ├── ffmpeg -f concat -i local.m3u8        → 拼接（本地文件，不连网）
            └── 清理临时目录
```

## 目录结构

```
DeepGrab/
├── Services/
│   └── VideoDownloadService.cs   # [MODIFY] 重写 DownloadWithFfmpegAsync
├── ViewModels/
│   └── MainViewModel.cs          # [MODIFY] 传入 HttpClient + 进度回调适配
└── Sites/
    └── PHSite.cs                 # [UNCHANGED] 解析逻辑无需改动
```