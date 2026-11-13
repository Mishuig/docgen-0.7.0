# Docugen (Document Generator)

Windows desktop application for importing DOCX templates, filling `{{placeholders}}`, and generating new Word documents. Microsoft Word is optional; it is used only if the user chooses to open a DOCX for manual editing/viewing.

## Що нового у 0.7.0 (Docugen)

- **Перехід на net9.0-windows** + EF Core 9 (стабільний portable self-contained; net10 не стартував на частині машин).
- Folder publish без single-file, Start-Docugen.cmd, ранній startup.log.


- **Швидший старт.** SQLite EnsureCreated лише при першому запуску; важкі діагностичні логи прибрані з hot path; каталог шаблонів вантажиться після першого кадру UI; папки templates/cache/exports створюються за потребою; publish з ReadyToRun.


- **Діагностика першого запуску.** При старті пишеться `startup.log` поруч із EXE (навіть якщо `logs/` ще не створено): ProcessPath, BaseDirectory, список файлів у папці, Runtime, помилки OnStartup покроково. Якщо програма не стартує — відкрийте `startup.log`.
- **Надійніший портативний запуск (0.6.2).** Native-бібліотеки (`e_sqlite3.dll` тощо) лежать поруч з EXE (`IncludeNativeLibrariesForSelfExtract=false`).
- **AppPaths (0.6.1):** директорія EXE через `Environment.ProcessPath`.

## Що було у 0.6.0

- **Кеш шаблонів.** Програма більше не працює з оригінальним `.docx` напряму: перегляд, підстановка й збереження йдуть із робочої копії в `cache/<id>/template.docx`. Оригінал лише читається зі спільним доступом, тому відкритий у Word файл не ламає програму. Копія оновлюється атомарно, якщо змінився SHA-256 оригіналу (також кнопкою «Оновити»). Якщо оригінал недоступний, працюємо з копією.
- **«Відкрити шаблон»** відкриває окрему копію з `cache/<id>/view`, а не оригінал.
- **Підтвердження** видалення шаблону й очищення полів (нове діалогове вікно у стилі Windows 11).
- **Редизайн у стилі Windows 11**: кольори з лого, лінійні іконки, власний заголовок вікна з лого, нові діалоги.
- Помилки роботи з файлами пишуться в `logs/application.log`.

## Current build status

Version 0.7.0 — Windows 11 (Fluent) redesign in the logo colours, template cache, confirmation dialogs. NOT compiled in the authoring environment (no .NET SDK there): run the GitHub Actions build and check the app before release. For a ready portable EXE use GitHub Actions (see README-GITHUB-BUILD.md) or `build-portable.cmd` on Windows.

### What is new in 0.7.0
- **More reliable portable launch.** Native libraries (`e_sqlite3.dll` etc.) stay next to the EXE (`IncludeNativeLibrariesForSelfExtract=false`). No TEMP extraction → fewer antivirus/permission failures.
- **AppPaths** (from 0.6.1): uses `Environment.ProcessPath` so data folders live next to the EXE.

### What was new in 0.6.0
- **Template cache** (`cache/<id>/template.docx`): preview, placeholder search and document generation work only on a working copy; the original `.docx` is read with shared access and never modified or locked, so Word/Explorer holding the file no longer breaks saving or opening. Integrity is checked by SHA-256, copying is atomic (temp file + replace), a half-written original keeps the previous copy, up to 3 retries on a busy file, fallback to the cache when the original is missing. «Відкрити шаблон» opens a separate copy from `cache/<id>/view`. «Оновити» refreshes the cache if the original changed. Cache is removed together with the template. Errors go to `logs/application.log`.
- **Confirmation dialogs** for deleting a template and clearing the fields (Esc cancels; for deletion the focus and Enter default to «Скасувати»).
- **Redesign**: shared theme `Themes/Theme.xaml` (palette, line icons, button/input/dialog styles), custom title bar with the original logo, icons on all main buttons, restyled dialogs. App name in the UI: Docugen.

### Implemented
- WPF desktop interface (Ukrainian UI).
- Portable data layout beside the executable: `data`, `templates`, `exports`, `backups`, `logs`, `cache`.
- Local SQLite catalog of templates.
- DOCX import with package validation.
- Search by template name/category.
- Template metadata editing (name/category).
- Safe library deletion: source file is moved into `backups` before its catalog record is deleted.
- Manual full backup of template files and SQLite database.
- Placeholder discovery in document body, tables, headers, footers, footnotes and endnotes.
- Placeholder replacement even when Word splits marker text across multiple runs.
- Non-breaking spaces for configured full-name fields and between `№` and following digits.
- Generated documents are created from a copy; imported source templates are not edited.
- Empty-field warning, output to `exports`, and optional opening with the default Windows application.
- Error logging to `logs/application.log`.

### Known limitations / next milestones
- The built-in template editor is not yet a Word-like rich-text editor. Use Word or another DOCX editor to lay out templates; the application manages, inspects and fills them.
- Advanced Word structures (text boxes, drawing objects, fields, tracked changes, content controls, nested tables and unusual run structures) need compatibility testing against real documents.
- No conditional/repeating sections, PDF preview/export, cloud synchronization, encryption, user roles or audit trail yet.
- Formatting rules such as non-breaking full names, number prefixes, dates and paragraph keep-with-next still need a dedicated rule engine and UI.

## Requirements
- Windows 10 (version 1809 or later) / Windows 11 x64.
- .NET 10 SDK to build from source. The published portable build is self-contained and does not require the .NET runtime installed.
- Microsoft Word is not required for generation.

## Run from source
```powershell
dotnet restore
dotnet run
```

## Publish portable Windows x64
Run on Windows (recommended) from the project folder:
```powershell
dotnet publish .\DocumentGenerator.csproj -c Release -r win-x64 --self-contained true -p:PublishSingleFile=true -p:IncludeNativeLibrariesForSelfExtract=true -p:EnableCompressionInSingleFile=true -o .\publish\win-x64
```

Copy the entire `publish\win-x64` folder to a writable location (for example, `C:\Apps\DocumentGenerator`) and run `DocumentGenerator.exe`. Keep the adjacent data folders with the application when moving it to another PC. Do not place the portable folder in `C:\Program Files` unless write permissions are configured; the app intentionally keeps data beside the EXE.

## Placeholder syntax
```text
ДОГОВІР № {{contract_number}}

Я, {{full_name}}, паспорт {{passport_number}},
проживаю за адресою: {{address}}.
```

Variable names: everything between `{{` and `}}` is a variable (any language, spaces, dots, quotes, symbols; empty `{{}}` too).

## Important
This is an in-progress application. Before using it for legally significant documents, test every template and generated output in Microsoft Word or LibreOffice, compare pagination, paragraph layout, tables, headers/footers, and verify all personal and financial details. Keep backups of original templates.

For a one-click build on Windows, double-click `build-portable.cmd`. It invokes the PowerShell publish script and places the self-contained application in `publish\win-x64`.


## Автоматична збірка через GitHub Actions

Інструкція українською: див. `README-GITHUB-BUILD.md`. Workflow `.github/workflows/build-windows.yml` збирає self-contained Windows x64 ZIP на GitHub Actions; локальний .NET SDK не потрібен.

## Ярлик
Після збірки/розпакування запустіть `Create-Shortcut.cmd` — він створить ярлик з логотипом на робочому столі та в меню «Пуск». Іконка самого `DocumentGenerator.exe` також оновлена (`App.ico`).

## Адаптивна верстка (0.5.4)
- Вікно можна зменшувати до 60×60 (Windows сам обмежує мінімум системною межею для вікон із заголовком).
- Шрифт і кнопки зменшуються разом із вікном; шрифт — не менше 10.
- Коли тексту не вистачає місця: спочатку прибираються слова з кінця, потім літери з додаванням «...».
- Розкладка: широке вікно — 3 колонки; половина екрана (≈400–760 px) — ліворуч бібліотека та дії, праворуч форма; вузьке — усе в один стовпчик; менше ~150 px — лише логотип.
