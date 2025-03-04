# ShowroomRecorder

****

| Author | HoshinoKun |
| ------ | ----------- |
| E-mail | hoshinokun@346pro.club |

****

[中文简介](/readme_cn.md)
## What's this?
An unattended Showroom recording tool based on C#  
Supported download backends: FFmpeg, minyami, minyami-iori, streamlink

## How to use
Rename [`example-config.yml`](/example-config.yml) to `config.yml`, and add the users you want to monitor to the `Users` section  
For `Downloader`, you can choose your preferred download backend

config.yml
```
DebugLog: false # Whether to enable debug log output to stdout
FileLog: false # Whether to enable file log recording, file logs are always at debug level
Interval: 20 # Monitoring interval for live broadcasts, in seconds
Downloader: none # Download backend to use, supported options are ffmpeg, minyami, streamlink. Make sure these tools are in your path. If the value specified is not among these options, the software will use its built-in downloader by default
Users: # Users you want to monitor
- user1
- user2
```