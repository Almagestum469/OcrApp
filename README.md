# OcrApp

A powerful Windows OCR (Optical Character Recognition) application built with WinUI 3 that supports screen capture, text recognition, and translation.

## Features

- 🖥️ **Screen Capture**: Select and capture any window or region on your screen
- 🔍 **Dual OCR Engines**:
  - **PaddleOCR**: Advanced AI-powered OCR with high accuracy
  - **Windows OCR**: Built-in Windows OCR engine
- 🌐 **Translation**: Automatic translation using Google Translate API
- 📋 **Region Selection**: Interactive region selection for precise text recognition
- 🪟 **Translation Overlay**: Floating, draggable translation window with pin functionality
- ⌨️ **Hotkey Support**: Quick access via keyboard shortcuts
- 🎯 **Smart Text Grouping**: Intelligent paragraph detection and organization

## Screenshots

_Add screenshots of your application here_

## Requirements

- Windows 10 version 1903 (build 18362) or later
- .NET 8.0
- Google Translate API key (for translation features)

## Installation

### From Source

1. Clone the repository:

```bash
git clone https://github.com/Almagestum469/OcrApp.git
cd OcrApp
```

2. Configure Google Translate API:

   - Copy `appsettings.example.json` to `appsettings.json`
   - Add your Google Translate API key:

   ```json
   {
     "GoogleTranslate": {
       "ApiKey": "YOUR_GOOGLE_TRANSLATE_API_KEY_HERE"
     }
   }
   ```

3. Build and run:

```bash
dotnet build
dotnet run
```

### Pre-built Releases

Download the latest release from the [Releases](https://github.com/Almagestum469/OcrApp/releases) page.

## Usage

1. **Select Capture Source**: Click "Select Window" to choose which window to capture
2. **Choose OCR Engine**: Select between PaddleOCR (recommended) or Windows OCR
3. **Select Region** (Optional): Click "Select Region" to define a specific area for recognition
4. **Recognize Text**: Click "Recognize" to perform OCR on the captured content
5. **View Translation**: Enable the translation overlay to see automatic translations

### Hotkeys

- **F1**: Quick capture and recognize
- **Esc**: Cancel current operation

## Configuration

The application can be configured via `appsettings.json`:

```json
{
  "GoogleTranslate": {
    "ApiKey": "your-api-key-here"
  }
}
```

## OCR Engines

### PaddleOCR

- Higher accuracy for complex text layouts
- Better support for rotated text
- Advanced paragraph detection
- Confidence scoring

### Windows OCR

- Built-in Windows functionality
- Faster processing
- No additional dependencies
- Good for simple text recognition

## Technical Details

- **Framework**: .NET 8.0 with WinUI 3
- **OCR Libraries**:
  - Sdcb.PaddleOCR for AI-powered recognition
  - Windows.Media.Ocr for native Windows OCR
- **Graphics**: Win2D for hardware-accelerated graphics processing
- **Screen Capture**: Windows.Graphics.Capture API

## Project Structure

```
OcrApp/
├── Engines/           # OCR engine implementations
│   ├── IOcrEngine.cs
│   ├── PaddleOcrEngine.cs
│   └── WindowsOcrEngine.cs
├── Utils/             # Utility classes
│   ├── ConfigManager.cs
│   └── GoogleTranslator.cs
├── MainWindow.xaml    # Main application window
├── RegionSelector.xaml # Region selection interface
├── TranslationOverlay.xaml # Translation overlay window
└── Assets/            # Application assets
```

## Dependencies

- Microsoft.WindowsAppSDK
- Sdcb.PaddleOCR
- Sdcb.PaddleInference
- Microsoft.Graphics.Win2D
- OpenCvSharp4

## Contributing

1. Fork the repository
2. Create a feature branch (`git checkout -b feature/amazing-feature`)
3. Commit your changes (`git commit -m 'Add amazing feature'`)
4. Push to the branch (`git push origin feature/amazing-feature`)
5. Open a Pull Request

## License

This project is licensed under the MIT License - see the [LICENSE](LICENSE) file for details.

## Acknowledgments

- [PaddleOCR](https://github.com/PaddlePaddle/PaddleOCR) for the excellent OCR engine
- [Sdcb.PaddleOCR](https://github.com/sdcb/PaddleSharp) for the .NET bindings
- Microsoft for the WinUI 3 framework and Windows OCR API

## Support

If you encounter any issues or have questions, please:

1. Check the [Issues](https://github.com/Almagestum469/OcrApp/issues) page
2. Create a new issue with detailed information
3. Include system information and error messages

## Roadmap

- [ ] Support for more OCR languages
- [ ] Batch processing capabilities
- [ ] Export results to various formats (PDF, Word, etc.)
- [ ] OCR result editing interface
- [ ] Custom hotkey configuration
- [ ] Dark theme support
- [ ] Multi-monitor support improvements

---
