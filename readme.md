# ShowroomRecorder

****

| Author | HoshinoKun |
| ------ | ----------- |
| E-mail | hoshinokun@346pro.club |

****

[中文简介](/readme_cn.md)
## What's this?
An unattended Showroom recording tool based on C#  
Supported download backends: built-in downloader, FFmpeg, minyami, streamlink

## How to use
This version reads configuration from `configs.yml`.  
If `configs.yml` does not exist, the program will create a default one automatically.  
Add the users you want to monitor to the `Users` section, then choose a download backend with `Downloader`.

configs.yml
```
DebugLog: false # Whether to enable debug log output to stdout
TraceLog: false # Whether to enable verbose/trace log output
FileLog: false # Whether to enable file log recording
Interval: 20 # Monitoring interval for live broadcasts, in seconds
Proxy: "" # Optional. HTTP/HTTPS/SOCKS5 proxy URL, for example http://127.0.0.1:7890 or socks5://127.0.0.1:1080
WebDavUrl: "" # Optional. WebDAV directory URL. Enable upload when URL is set and upload test succeeds
WebDavUsername: "" # Optional. Leave empty for anonymous access
WebDavPassword: "" # Optional. If either username/password is provided, Basic auth will be used
WebDavAllowInsecureCertificate: false # Optional. Ignore invalid/self-signed WebDAV TLS certificates
Downloader: none # Download backend to use. Supported values: ffmpeg, minyami, streamlink. Any other value uses the built-in downloader
Users: # Users you want to monitor
- user1
- user2
SrId: "" # Optional. Showroom sr_id cookie value, used for follow-room related requests
```
