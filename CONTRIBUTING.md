# Contributing to AI Usage Tracker

Thank you for considering contributing to **AI Usage Tracker**! We welcome bug fixes, documentation improvements, new AI provider integrations, and UI enhancements.

## Development Setup

### Prerequisites
- **Windows 10 / 11 (64-bit)**
- **[.NET 10.0 SDK](https://dotnet.microsoft.com/download/dotnet/10.0)**
- **Visual Studio 2022 / 2025** (with Desktop development with .NET workload) or **VS Code** / **JetBrains Rider**

### Building and Running
1. Clone the repository:
   ```bash
   git clone https://github.com/LeGustaVinho/ai-usage-tracker.git
   cd ai-usage-tracker
   ```
2. Restore dependencies:
   ```bash
   dotnet restore
   ```
3. Build the solution:
   ```bash
   dotnet build
   ```
4. Run the automated unit tests:
   ```bash
   dotnet test
   ```
5. Run the application:
   ```bash
   dotnet run --project src/AIUsageTracker
   ```

## Pull Request Guidelines

1. **Branch Naming**: Use descriptive branch names like `feat/support-ollama` or `fix/claude-oauth-retry`.
2. **Coding Standards**:
   - Follow standard modern C# conventions (C# 13, nullable reference types enabled, file-scoped namespaces).
   - Use MVVM pattern with `CommunityToolkit.Mvvm`.
   - Preserve non-blocking async operations — never block the UI thread (`Dispatcher`).
3. **Tests**: Ensure that existing unit tests pass (`dotnet test`) and write new tests for bug fixes or new quota providers.
4. **Clean Commits**: Keep commit messages concise, descriptive, and written in English (e.g., `feat: ...`, `fix: ...`, `docs: ...`).

## Reporting Bugs & Suggesting Features

- Use GitHub Issues to submit bug reports and feature requests.
- Please provide as much context as possible (environment details, provider versions, reproduction steps).

Thank you for helping make AI Usage Tracker better!
