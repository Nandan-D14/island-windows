<div align="center">

  <img src="logo.png" alt="Island Logo" width="128" height="128" />

  # Island for Windows
  
  **An elegant Dynamic Island & multimodal Gemini AI companion for Windows 10 & 11.**

  [![Version](https://img.shields.io/badge/version-1.5.3-blue.svg?style=flat-square)](https://github.com/Nandan-D14/island-windows/releases)
  [![Platform](https://img.shields.io/badge/platform-Windows%2010%20%7C%2011%20(x64)-0078D6.svg?style=flat-square&logo=windows)](https://github.com/Nandan-D14/island-windows)
  [![.NET](https://img.shields.io/badge/.NET-9.0-512BD4.svg?style=flat-square&logo=dotnet)](https://dotnet.microsoft.com/download/dotnet/9.0)
  [![License](https://img.shields.io/badge/license-PolyForm%20Noncommercial-green.svg?style=flat-square)](LICENSE)
  [![AI](https://img.shields.io/badge/AI-Google%20Gemini%20Live-orange.svg?style=flat-square&logo=google)](https://ai.google.dev/)

  <p align="center">
    <a href="#-quick-download--try-it"><b>Download Island (.zip)</b></a> •
    <a href="#-features"><b>Features</b></a> •
    <a href="#-getting-started"><b>Getting Started</b></a> •
    <a href="#-system-controls--tools"><b>PC Controls</b></a> •
    <a href="#-building-from-source"><b>Build Source</b></a> •
    <a href="#-license"><b>License</b></a>
  </p>

</div>

---

## 🚀 Quick Download & Try It

Get up and running in under a minute — no complicated installer required!

| Distribution Package | Download Link | Notes |
| :--- | :--- | :--- |
| **Island v1.5.3 (Portable / Installer Zip)** | [**Download Island-v1.5.3-Windows-x64.zip**](https://github.com/Nandan-D14/island-windows/releases/download/v1.5.3/Island-v1.5.3-Windows-x64.zip) | Single-file executable ready to run |

### ⚡ 30-Second Quick Start:
1. **[Download the ZIP archive](https://github.com/Nandan-D14/island-windows/releases/download/v1.5.3/Island-v1.5.3-Windows-x64.zip)** and extract it to any folder.
2. Double-click **`Island.exe`**:
   - Click **Yes** to install it into your user profile (creates a Start Menu shortcut and auto-starts with Windows).
   - Or click **No** to run it in **100% portable mode** without modifying your system.
3. Move your mouse to the **top-center of your screen** — the Dynamic Island will smoothly glide open!
4. Click the **Gear icon (Settings)** to enter your free [Google Gemini API Key](https://aistudio.google.com/app/apikey).

---

## ✨ Features

### 🏝️ Fluid Dynamic Island Interface
* **Discreet Floating Pill**: Sits silently at the top of your monitor and auto-hides when you're working or gaming.
* **Liquid Animations**: Expands smoothly on hover or hotkey into an all-in-one control center.
* **Now Playing Widget**: Displays current track name, artist, interactive playback controls, and album art.
* **Real-time System Stats**: Live CPU load, RAM usage, Wi-Fi status, and battery percentage right in the notch.

### 🧠 Gemini Multimodal Intelligence
* **Conversational AI**: Powered by Google's Gemini models for intelligent search, summaries, translations, and answers.
* **Real-time Web Search**: Island queries the web directly for live scores, breaking news, weather forecasts, and stock prices.
* **Memory & Natural Context**: Talks naturally like a helpful companion, adapting to your language (English, Hindi, Kannada, and more).

### 🎙️ Gemini Live Voice Assistant
* **Bidirectional Voice Streaming**: Low-latency voice conversations powered by the Gemini Live API.
* **Wake Word Detection**: Simply say *"Hey Gemini"* to wake up the assistant completely hands-free.
* **Smart Audio Ducking**: Automatically attenuates background music and media playback while speaking with Gemini.
* **Visual Sound Waveforms**: Real-time reactive waveform animations while listening and responding.

### 👁️ Screen Vision ("Look at my screen")
* **Visual Contextual Q&A**: Ask Gemini: *"What does this error message mean?"*, *"Summarize this article"*, or *"Debug this code"*. Island captures your screen and answers instantly.
* **Live Screen Sharing**: Stream your screen live during voice sessions for real-time guidance.
* **Built-in Color Picker**: Pick any pixel on your monitor and automatically copy the hex code.

### 🎛️ Native PC Control & Tools
Island can interact with Windows natively through secure, verified tool calls:
* 🔊 **Audio**: Set volume percentage, mute/unmute, adjust audio relatively (*"make it a bit louder"*).
* ☀️ **Display**: Adjust screen brightness with precision.
* 📶 **Connectivity**: Toggle Wi-Fi and Bluetooth on or off.
* ⏱️ **Focus & Timers**: Countdown timers, stopwatch, and a Pomodoro **Focus Mode** that silences distractions.
* 🔒 **Power & Security**: Lock your PC, initiate sleep, restart, or power down safely.
* 📁 **Navigation**: Launch installed apps, open system folders (Downloads, Documents, Desktop), or visit URLs.

### 📋 Productivity Hub
* **To-Do List**: Manage daily tasks with natural voice commands (*"Add buy milk to my todos"*).
* **Quick Scratchpad Notes**: Jot down phone numbers, code snippets, and ideas on the fly.
* **Smart Shelf**: Drag-and-drop temporary storage for images, snippets, and files.
* **Markdown Chat Drawer**: Full conversation view with rich markdown formatting, copy buttons, and code blocks.

### 📱 Phone Remote Control
* Scan the generated QR code in Island using your smartphone browser to access a sleek mobile remote over your local network.

---

## 🛠️ System Requirements

* **OS**: Windows 10 (version 19041 / 20H1 or newer) or Windows 11 (64-bit).
* **Runtime**: [.NET 9.0 Desktop Runtime](https://dotnet.microsoft.com/download/dotnet/9.0) (x64).
* **Browser Runtime**: Microsoft Edge WebView2 (included with Windows 10 & 11).
* **Microphone & Speakers**: Required for Gemini Live voice features.

---

## ⌨️ Hotkeys & Shortcuts

| Shortcut | Action |
| :--- | :--- |
| **Double-tap <kbd>Alt</kbd>** | Activate Gemini Voice / Wake Island *(Configurable to <kbd>Ctrl</kbd> or Off)* |
| **Hover Top-Center** | Expand the Dynamic Island control deck |
| **<kbd>Esc</kbd>** | Close Chat Popup / Cancel active voice or typing session |
| **<kbd>Enter</kbd>** | Submit prompt in chat box or add task in To-Do list |

---

## 💻 Building from Source

If you prefer to compile Island yourself:

### 1. Clone the repository
```bash
git clone https://github.com/Nandan-D14/island-windows.git
cd island-windows
```

### 2. Restore and Build
```bash
dotnet restore
dotnet build -c Release
```

### 3. Publish Single-File Executable
```bash
dotnet publish -c Release -r win-x64 --self-contained false -p:PublishSingleFile=true -o ./publish
```

The resulting `Island.exe` will be generated in `./publish`.

---

## 🎛️ Command-Line Arguments

| Flag | Description |
| :--- | :--- |
| `Island.exe` | Default launch mode (prompts to install if run outside the install folder). |
| `Island.exe --portable` | Runs directly without checking or prompting for system installation. |
| `Island.exe --update` | Silently updates an existing installation. |
| `Island.exe --uninstall` | Completely removes Island from your system (Start Menu and App registration). |
| `Island.exe --quiet` | Suppresses confirmation dialogs during setup or uninstallation. |

---

## 📂 Architecture & Code Structure

```
Island/
├── MainWindow.xaml / .cs    # Main transparent overlay window, animations & pill layout
├── App.xaml / .cs           # Application entry point, single-instance mutex, CLI router
├── Gemini.cs                # Google Gemini API REST client & streaming protocol
├── Live.cs                  # Gemini Multimodal Live WebSocket client (voice & screen stream)
├── Assistant.cs             # Tool definitions, prompt engineering & tool execution dispatch
├── Abilities.cs             # Native Windows control (Volume, Brightness, Bluetooth, Wi-Fi)
├── WakeWord.cs              # Audio input capture & wake-word detection engine
├── VoiceUi.cs               # Sound visualizer & animated waveform rendering
├── ScreenGrab.cs / Shots.cs # Screen capture, snipping integration & image analysis
├── ChatPop.cs               # Detachable markdown chat drawer window
├── Shelf.cs                 # Drag-and-drop clipboard shelf & temporary file holder
├── Notes.cs                 # Persistent user scratchpad notes
├── Power.cs                 # Workstation lock, sleep, restart, shutdown handlers
├── Remote.cs                # Web server & QR code generator for phone remote control
├── Safety.cs                # Action verification & safety boundaries for tool execution
└── Installer.cs             # Zero-dependency self-installer and auto-updater
```

---

## 🤝 Contributing

Contributions, bug reports, and suggestions are warmly welcomed! Please read our [**Contributing Guide**](CONTRIBUTING.md) and [**Code of Conduct**](CODE_OF_CONDUCT.md) before submitting pull requests.

---

## 📜 License

This project is licensed under the **[PolyForm Noncommercial License 1.0.0](LICENSE)**.

* ✅ **Permitted**: Free for personal use, educational purposes, academic research, evaluation, and testing.
* ❌ **Prohibited**: Commercial exploitation, selling, leasing, or charging fees for the software or services derived from it without prior explicit written agreement.

Copyright © 2026 **Nandan-D14** and contributors.
