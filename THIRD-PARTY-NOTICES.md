# 第三方组件 / Third-party notices

视搭 VisionBlocks 本身按 MIT 许可证发布（见 [LICENSE](LICENSE)）。它用到下面这些第三方组件，各自的许可证照原样适用；发布包（便携 zip、单个 exe）里带着其中的运行时部分。

VisionBlocks itself is released under the MIT License (see [LICENSE](LICENSE)). It uses the third-party components below, each under its own license; the release packages (portable zip and single exe) include their runtime parts.

| 组件 / Component | 用途 / Used for | 许可证 / License |
|---|---|---|
| [.NET runtime and Windows Desktop (WPF)](https://github.com/dotnet/runtime) | 运行时，发布包是自包含的 / runtime, bundled in the self-contained packages | MIT |
| [OpenCvSharp4](https://github.com/shimat/opencvsharp) 4.13 | 模板匹配、颜色范围 / template matching, color ranges | Apache-2.0 |
| [OpenCV](https://opencv.org/)（随 OpenCvSharp4.runtime.win 分发 / shipped in OpenCvSharp4.runtime.win） | 同上，原生库 / same, native library | Apache-2.0 |
| [Vortice.Direct3D11](https://github.com/amerkoleci/Vortice.Windows) 3.8（含 / incl. SharpGen.Runtime） | Windows Graphics Capture 截图 / screen capture | MIT |
| [Microsoft.Windows.CsWin32](https://github.com/microsoft/CsWin32) | 编译时生成 Win32 调用，不随发布包分发 / build-time Win32 interop generator, not shipped | MIT |
| Windows SDK projections（Microsoft.Windows.SDK.NET） | Windows.Media.Ocr、截图 API / OCR and capture APIs | MIT |

发布包里去掉了 OpenCV 自带的 `opencv_videoio_ffmpeg`（视频读写用不到）。
The release packages leave out OpenCV's `opencv_videoio_ffmpeg` (video I/O is not used).

文字识别用的是 Windows 自带的 OCR（Windows.Media.Ocr），不随本项目分发。
Text recognition uses the OCR built into Windows (Windows.Media.Ocr); it is not distributed with this project.

应用图标和横排 logo 的字形由 [Outfit](https://fonts.google.com/specimen/Outfit) 和 [Noto Sans SC](https://fonts.google.com/noto/specimen/Noto+Sans+SC) 转成路径（SIL Open Font License 1.1）；仓库和发布包里不带字体文件。
The app logo's lettering was converted to outlines from Outfit and Noto Sans SC (SIL Open Font License 1.1); no font files are included.

截图、遮罩窗口和键鼠输入的技术选型参考过开源项目 [BetterGI](https://github.com/babalae/better-genshin-impact)（GPL-3.0）公开的做法。本项目的代码是独立编写的，没有从 BetterGI 复制代码（发布前对照过它的截图、遮罩、输入、热键相关源文件）。
The choice of capture, overlay-window and input techniques took ideas from the public approach of the open-source project BetterGI (GPL-3.0). The code was written independently and none was copied from BetterGI (checked against its capture, overlay, input and hotkey source files before release).
