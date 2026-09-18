# AudioBar 音频条

AudioBar 是一款运行于 Windows 平台的桌面音频可视化工具。通过 WASAPI loopback 捕获系统输出混音，在任务栏上方以频谱条形呈现实时音频，采用每柱峰值归一化、多频段独立检测与软饱和曲线，动态层次清晰稳定。

![效果预览](screenshot.png)

## 功能特性

- **每柱峰值归一化**：各条独立维护包络（快起慢落），各频段动态互不干扰。
- **多频段独立检测**：低频、人声主体（300 Hz–3 kHz）、齿音区（2–6 kHz）、高频四段独立跟踪，避免人声、旋律与瞬态抢占视觉空间。
- **高音高亮**： presence 频段 / 镲片 / 高 solo 出现时柱子弹闪白，退去自动回到壁纸渐变色。
- **软饱和动态曲线**：持续音停在中等高度，只有鼓点等瞬态才能冲顶，消除"整条贴顶成线"的问题。
- **可靠捕获生命周期**：设备热插拔自动重连、看门狗恢复、命名互斥体保证单实例运行。

## 运行环境

- Windows 10 / 11 x64
- .NET 9 Desktop Runtime（发布版已自包含，无需额外安装）

## 编译

```bash
dotnet build -c Release
```

产物路径：`bin\Release\net9.0-windows\AudioBar.exe`

如需发布为自包含单文件（推荐分发方式）：

```bash
dotnet publish -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:EnableCompressionInSingleFile=true
```

## 运行

```bash
dotnet run -c Release
```

或直接运行发布产物 `AudioBar.exe`。

## 许可证

待定
