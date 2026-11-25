# Настройка и запуск на Windows

## 1. Требования

- Windows x64.
- .NET 8 SDK: `dotnet --version` должен показать 8.x.
- Для Visual Studio: VS 2022 17.8+ и нагрузка «Разработка классических приложений .NET» / .NET desktop development.
- Для GPU-инференса: совместимый драйвер и выбранный бэкенд CUDA12 или Vulkan.
- GGUF-модель отдельно; в репозиторий она не входит.

## 2. Сборка

Из корня репозитория:

```powershell
dotnet restore AITradingSystem.sln
dotnet build AITradingSystem.sln --configuration Release
dotnet test AITradingSystem.Tests/AITradingSystem.Tests.csproj --configuration Release
```

По умолчанию UI-проект тянет CPU + CUDA12 + Vulkan native-пакеты LLamaSharp. Это может занимать много места. Варианты:

```powershell
# Только CPU
dotnet build AITradingSystem.sln --configuration Release -p:LlamaGpuBackends=none

# NVIDIA CUDA12 + CPU
dotnet build AITradingSystem.sln --configuration Release -p:LlamaGpuBackends=cuda12

# AMD/Intel/NVIDIA Vulkan + CPU
dotnet build AITradingSystem.sln --configuration Release -p:LlamaGpuBackends=vulkan
```

Для очень старого CPU без AVX2 можно добавить `-p:LlamaCpuAvxLevel=avx` или `-p:LlamaCpuAvxLevel=noavx`.

## 3. Запуск

Через Visual Studio: откройте `AITradingSystem.sln`, назначьте `AITradingSystem.UI` стартовым проектом и запустите.

Через CLI:

```powershell
dotnet run --project AITradingSystem.UI/AITradingSystem.UI.csproj --configuration Release
```

Приложение читает `appsettings.json` и `symbols.json` из папки рядом с exe, а не обязательно из корня исходников.

## 4. Режимы и ключи

В поставленном шаблоне:

- `Binance:Environment = "Demo"`;
- `Trading:PaperTradingMode = false`.

Это Demo-биржа с виртуальными средствами, но с реальной отправкой ордеров в Demo-окружение. `PaperTradingMode=true` включает симуляцию ордеров движком. `Live` — реальные деньги.

Ключи Binance хранятся в зашифрованном виде. На Windows используется DPAPI текущего пользователя: шифруйте и запускайте под тем же аккаунтом Windows и на той же машине.

Утилита шифрования не входит в основное solution и запускается отдельно:

```powershell
dotnet run --project SecretEncryptor/SecretEncryptor.csproj
```

Она запрашивает API Key и Secret Key и выводит зашифрованные строки. В текущей конфигурации поля называются:

```json
"Binance": {
  "EncryptedApiKey": "",
  "EncryptedApiSecret": "",
  "EncryptedLiveApiKey": "",
  "EncryptedLiveApiSecret": ""
}
```

Внимание: консольная утилита в исходниках печатает подсказки `ApiKeyEncrypted` / `ApiSecretEncrypted`, но текущая модель конфигурации использует `EncryptedApiKey` / `EncryptedApiSecret`. Копируйте значения в поля текущей конфигурации. Для Live предназначены отдельные поля `EncryptedLiveApiKey` / `EncryptedLiveApiSecret`.

Не вставляйте открытые API-ключи в Git. Личные зашифрованные значения, логи, состояние и модели тоже не стоит публиковать.

## 5. Модель и native-библиотеки

В шаблоне указан путь:

```json
"AI": { "ModelPath": "models/qwen3-14b-q4_k_m.gguf" }
```

Положите модель по этому пути относительно папки запуска или измените `AI:ModelPath`. Нативные библиотеки LLamaSharp раскладываются сборкой UI-проекта; при необходимости задайте `AI:NativeLibraryPath`.

## 6. Что не коммитить

`.gitignore` исключает `bin/`, `obj/`, `logs/`, `data/`, `models/`, `*.gguf`, локальные override-конфигурации, резервные копии и исходные ZIP-архивы проекта. После настройки проверяйте `git status` и `git diff --cached` перед коммитом.
