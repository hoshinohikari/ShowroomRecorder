# ShowroomRecorder

****

| Author | HoshinoKun |
| ------ | ----------- |
| E-mail | hoshinokun@346pro.club |

****

## What's this?
一个基于C#的无人值守Showroom录制机  
支持下载后端：内置下载器、FFmpeg、minyami、streamlink

## How to use
当前版本读取的配置文件名为`configs.yml`。  
如果 `configs.yml` 不存在，程序会自动创建默认配置文件。  
将你想监控的用户添加到 `Users` 中，并通过 `Downloader` 选择下载后端。

configs.yml
```
DebugLog: false # 是否打开 stdout 的 debug 日志输出
TraceLog: false # 是否打开更详细的 trace/verbose 日志输出
FileLog: false # 是否打开文件日志记录
Interval: 20 # 监控开播的等待时间间隔，单位为秒
Proxy: "" # 可选。HTTP/HTTPS/SOCKS5 代理地址，例如 http://127.0.0.1:7890 或 socks5://127.0.0.1:1080
WebDavUrl: "" # 可选。WebDAV目录地址，配置后且上传测试成功即启用上传
WebDavUsername: "" # 可选。留空时尝试匿名访问
WebDavPassword: "" # 可选。若账号/密码任一项有值，则使用 Basic 认证
WebDavAllowInsecureCertificate: false # 可选。是否允许忽略 WebDAV 的无效/自签名 TLS 证书
Downloader: none # 使用的下载器后端，支持 ffmpeg、minyami、streamlink。若填写其他值则默认使用软件内置下载器
Users: # 你需要监控的用户名
- user1
- user2
SrId: "" # 可选。Showroom 的 sr_id cookie，用于关注房间相关请求
```
