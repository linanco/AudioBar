# 🎵 AudioBar — Windows 桌面音频可视化条

贴在 Windows 任务栏上方的实时音频频谱条。随音乐节奏跳动，支持鼠标 hover 局部收起、高音白化、多频段独立增益。

![screenshot](https://raw.githubusercontent.com/linanco/AudioBar/main/screenshot.png)

## ✨ 特性

- **真实"活"的频谱** — 每根柱子独立包络（攻击 / 释放分离），不会整条一起贴顶，人声、鼓点、高音层次分明
- **多频段独立检测** — 低音、人声、高音各有自己的能量包络，不会互相淹没
- **高音白化** — 高音/镲片/齿音一冲，柱子瞬间闪白
- **鼠标局部 hover 收起** — 鼠标靠近音频条时，只有指向的那一小撮柱子缩下去（高斯权重衰减），其他照常跳动
- **桌面取色** — 柱子颜色每 3 秒抓取屏幕中部色带，随壁纸/动态壁纸实时渐变
- **设备热插拔** — 音响/耳机切换自动重连，不用重启

## 📥 下载

**最新稳定版：[v1.0.4](https://github.com/linanco/AudioBar/releases/tag/v1.0.4)**

- [AudioBar.exe](https://github.com/linanco/AudioBar/releases/download/v1.0.4/AudioBar.exe) — 自包含单文件，免装 .NET 直接运行（~48 MB）

## 🖱️ 交互

| 操作 | 效果 |
|------|------|
| 托盘双击 | 显示 / 隐藏主面板 |
| 托盘右键 → 打开主面板 | 进入调参面板（低音、人声、高音增益 / 鼓点灵敏度 / 软饱和等） |
| 鼠标移到音频条 | 局部柱子收起，离开后弹回 |

## 🛠️ 从源码编译

需要 .NET 9 SDK：

`ash
dotnet build -c Release
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true
`

## 📁 项目结构

`
AudioBar/
├── VisualizerForm.cs     # 主窗口、绘图、hover、高音白化
├── AudioCapture.cs       # WASAPI 循环捕获 + 频谱算法
├── AudioSettings.cs      # 可调参数数据类 + 持久化（INCOMPLETE）
├── SettingsForm.cs       # 调参主面板
├── DefaultDeviceClient.cs
└── GraphicsExtensions.cs # 圆角顶部绘制
`

## ⚙️ 算法亮点

- **相对每柱峰值归一化** — 不用全局自动增益，人声不会被低频垫音淹没
- **高斯 hover 权重** — exp(−dist² / (2σ²))，sigma=3.5，影响半径约 ±10 根柱
- **软饱和防贴顶** — 持续音停在中等高度，瞬态鼓点才冲顶
- **慢速自动增益** — 只随整体音量小幅调节，不跟着每帧乱跳
- **4096 汉宁窗 + 1024 重叠** — 兼顾频率分辨率和帧率

## 📜 License

[MIT](LICENSE)