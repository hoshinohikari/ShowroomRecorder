# ShowroomRecorder

****

| Author | HoshinoKun |
| ------ | ----------- |
| E-mail | hoshinokun@346pro.club |

****

## What's this?
一个基于C#的无人值守Showroom录制机  
支持下载后端：FFmpeg、minyami、minyami-iori、streamlink

## How to use
将[`example-config.yml`](/example-config.yml)重命名为`config.yml`，将你想监控的用户添加到`Users`里面  
`Downloader`可以选择你喜欢的下载后端

config.yml
```
DebugLog: false # 是否打开stdout的debug日志内容输出
FileLog: false # 是否打开文件log记录，文件log始终为debug级
Interval: 20 # 监控开播的等待时间间隔，单位为秒
Downloader: none # 使用的下载器后端，支持的选项为ffmpeg、minyami、streamlink，请保证使用时这些工具在你的path中，如果填写的不是这几个值则默认采用软件自有的下载器
Users: # 你需要监控的用户名
- user1
- user2
```