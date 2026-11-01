# Contributing to Island for Windows

Thank you for your interest in contributing to **Island**! We welcome community contributions, suggestions, and improvements from developers, designers, and enthusiasts.

Please take a moment to review this document before submitting issues or pull requests.

---

## 📜 Non-Commercial License Notice

Please note that this project is licensed under the **PolyForm Noncommercial License 1.0.0**. By contributing to this repository, you agree that your contributions will be licensed under the same non-commercial terms.

---

## 🛠️ Development Setup

### Prerequisites

To build and run Island from source, ensure your environment meets the following requirements:

1. **Operating System**: Windows 10 (Build 19041+) or Windows 11 (64-bit).
2. **.NET SDK**: [.NET 9.0 SDK](https://dotnet.microsoft.com/download/dotnet/9.0) or higher.
3. **IDE / Editor**:
   - [Visual Studio 2022](https://visualstudio.microsoft.com/) (version 17.12+) with the **.NET Desktop Development** workload installed.
   - Or [Visual Studio Code](https://code.visualstudio.com/) with the **C# Dev Kit** extension.
   - Or [JetBrains Rider](https://www.jetbrains.com/rider/).
4. **WebView2 Runtime**: Included with Windows 10/11, or install the [WebView2 Evergreen Bootstrapper](https://developer.microsoft.com/en-us/microsoft-edge/webview2/).

### Cloning and Building

```powershell
# 1. Clone your fork
git clone https://github.com/<your-username>/island-windows.git
cd island-windows

# 2. Restore NuGet dependencies
dotnet restore

# 3. Build in Debug mode
dotnet build

# 4. Run the project in development mode
dotnet run
```

> [!NOTE]
> When running from `bin/Debug/`, Island automatically recognizes that it is in development mode and will not prompt to install itself to your system.

---

## 🧭 Codebase Overview

Here is a quick map of the key components to help you navigate:

| File / Component | Purpose |
| :--- | :--- |
| `App.xaml` / `App.xaml.cs` | Application lifecycle, single-instance enforcement, mutex, command-line arguments. |
| `MainWindow.xaml` / `MainWindow.xaml.cs` | Core Dynamic Island window, animations, layout, expand/collapse pill state. |
| `Gemini.cs` | Google Gemini API client, text & multimodal inference, streaming response handling. |
| `Live.cs` | Gemini Multimodal Live API client (WebSocket audio/video streaming, voice activity). |
| `Assistant.cs` | System prompts, tool schemas, function calling registry, conversational logic. |
| `Abilities.cs` | System controls (volume, brightness, media keys, Wi-Fi, Bluetooth, timers). |
| `ScreenGrab.cs` / `Shots.cs` | Screen capture, snipping tool integration, OCR and visual analysis. |
| `WakeWord.cs` / `VoiceUi.cs` | Microphone audio capture, wake-word listener, dynamic waveform animation. |
| `ChatPop.cs` | Detachable / pop-out markdown chat drawer. |
| `Shelf.cs` | Drag-and-drop clipboard shelf and temporary file holder. |
| `Notes.cs` | Persistent scratchpad and quick note-taking system. |
| `Installer.cs` | Self-contained installer, Start menu integration, auto-updater, and clean uninstaller. |

---

## 🔄 Contribution Workflow

1. **Fork the Repository**: Fork the repository on GitHub to your account.
2. **Create a Feature Branch**:
   ```bash
   git checkout -b feature/amazing-feature
   # or
   git checkout -b fix/issue-description
   ```
3. **Make Your Changes**:
   - Keep changes concise, modular, and focused on one problem or feature.
   - Ensure the application builds cleanly with `dotnet build` with zero warnings or errors.
4. **Test Thoroughly**:
   - Verify UI responsiveness on standard DPI and high DPI screens.
   - Test mouse hover, keyboard navigation, and edge cases.
   - Verify that your changes do not push any sensitive `.env` files or API keys.
5. **Commit Your Code**:
   - Write clear, descriptive commit messages following standard conventional commits:
     - `feat: add custom hotkey configuration`
     - `fix: resolve sound ducking issue when audio ends`
     - `docs: update installation instructions`
6. **Submit a Pull Request**:
   - Push your branch to GitHub:
     ```bash
     git push origin feature/amazing-feature
     ```
   - Open a Pull Request against the `main` branch of `Nandan-D14/island-windows`.
   - Provide a clear summary of your changes, motivation, and screenshots/GIFs for UI changes.

---

## 🎨 Code Style & Guidelines

- **C# Language Version**: Modern C# (C# 13 / .NET 9). Use pattern matching, file-scoped namespaces, collection expressions (`[...]`), and target-typed `new()`.
- **WPF / UI**:
  - Keep styling consistent with the dark, glassmorphism, rounded pill design language.
  - Animations should use smooth easing (`QuadraticEase` or `CubicEase`) with short durations (200ms–350ms).
- **Tool Safety**: Any new PC control tools registered in `Assistant.cs` must check permissions and follow safe execution patterns defined in `Safety.cs`.

---

## 🐛 Reporting Bugs & Requesting Features

- Use GitHub [Issues](https://github.com/Nandan-D14/island-windows/issues).
- Before creating a new issue, check existing open and closed issues to avoid duplicates.
- When filing a bug report, please include:
  - Windows version and build number (e.g., Windows 11 23H2).
  - Steps to reproduce the bug.
  - Expected vs. actual behavior.
  - Relevant logs or screenshots.

---

## 🤝 Code of Conduct

All contributors and participants are expected to adhere to our [Code of Conduct](CODE_OF_CONDUCT.md). Please be respectful, welcoming, and collaborative!
