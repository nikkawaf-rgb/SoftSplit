# Разработка

Правила, отладка и процесс работы над проектом.

---

## 1. Правило номер один: `.ps1` — только ASCII

Windows PowerShell 5.1 читает файл `.ps1` без BOM как **ANSI (CP1251)**. Если в скрипте есть
кириллица, она превращается в мусор вида `РЎРІРѕР±РѕРґРЅРѕ`, и парсер падает с
`UnexpectedToken` на совершенно невинных строках.

Поэтому:

- **все `.ps1` и `.cmd` — чистый ASCII**, сообщения на английском;
- русский язык живёт в `.md` (UTF-8) — GitHub и редакторы читают его корректно;
- если очень нужно русское сообщение в скрипте — добавляйте BOM (`UTF8 with BOM`) и
  проверяйте на PowerShell 5.1, а не на 7.

Проверка перед коммитом:

```powershell
Get-ChildItem *.ps1, *.cmd | ForEach-Object {
    $bad = Select-String -Path $_.FullName -Pattern '[^\x00-\x7F]'
    if ($bad) { "{0}: {1} не-ASCII строк" -f $_.Name, $bad.Count }
}
```

Или через ripgrep:

```
rg -n "[^\x00-\x7F]" -g "*.ps1" -g "*.cmd"
```

---

## 2. Структура

```
SoftSplit/
├─ mirror-on.cmd          # запуск зеркала
├─ mirror-off.cmd         # остановка
├─ fix-layout.cmd         # аварийное восстановление топологии
├─ MirrorWindow.ps1       # ядро зеркала (окно + захват + CCD-имена)
├─ MirrorDisplay.ps1      # статус топологии / возврат к extend
├─ docs/                  # документация
├─ README.md
├─ CHANGELOG.md
├─ ROADMAP.md
└─ LICENSE
```

Запускаемые пользователем точки входа — только `.cmd`. Всё остальное — внутренности.

---

## 3. Запуск и отладка

```powershell
# обычный запуск (окно появится на телевизоре)
powershell -ExecutionPolicy Bypass -File .\MirrorWindow.ps1 -Fps 60

# посмотреть, что видит система
powershell -ExecutionPolicy Bypass -File .\MirrorDisplay.ps1 -Action status

# получить вывод в файл (скрипт блокирующий, окно закроется по Esc)
powershell -ExecutionPolicy Bypass -File .\MirrorWindow.ps1 *> mirror.log
```

Отладка окна «на глаз»:

```powershell
# снимок области телевизора в файл
Add-Type -AssemblyName System.Drawing
$bmp = New-Object System.Drawing.Bitmap 3840, 2160
$g = [System.Drawing.Graphics]::FromImage($bmp)
$g.CopyFromScreen(4480, 0, 0, 0, $bmp.Size)
$bmp.Save("$PWD\tv.png", [System.Drawing.Imaging.ImageFormat]::Png)
```

Полезно помнить:

- `Add-Type` компилирует C# при первом запуске — 1–3 секунды задержки это норма;
- после правки `.ps1` ничего пересобирать не нужно, скрипт интерпретируется;
- `$LASTEXITCODE` и `exit 1` используются для сигнализации ошибок в `.cmd`-обёртках.

---

## 4. Как проверять изменения

Минимальный чек-лист перед коммитом:

1. `MirrorDisplay.ps1 -Action status` печатает три строки с корректными режимами
   (имя, source id, разрешение источника и приёмника).
2. `mirror-on.cmd` → на телевизоре появляется окно с рабочим столом основного монитора,
   занимает **весь** экран (а не четверть).
3. Повторный `mirror-on.cmd` → `Already running`, второго окна нет.
4. `mirror-off.cmd` → окно закрылось, PID-файл удалён.
5. Не-ASCII в `.ps1`/`.cmd` отсутствует.

---

## 5. Git-процесс

```powershell
git status
git add -A
git commit -m "feat: короткое описание"
git push
```

Соглашения по сообщениям (упрощённый Conventional Commits):

| Префикс | Когда |
|---|---|
| `feat:` | новая функциональность |
| `fix:` | исправление |
| `docs:` | только документация |
| `refactor:` | перестройка без изменения поведения |
| `chore:` | служебное (gitignore, обёртки) |

Ветки: `main` — рабочая; под фичи — `feature/gui`, `feature/wgc-capture` и т. п.
Теги — по версиям: `v0.1.0`, `v0.2.0`.

---

## 6. Как добавлять графическую оболочку

План (см. [ROADMAP.md](../ROADMAP.md)):

1. **Вынести ядро.** Функции `Get-DisplaysInfo`, `Start-Mirror`, `Stop-Mirror` — в модуль
   `SoftSplit.Core.psm1`; `.cmd` продолжают работать через него.
2. **Конфиг.** `%APPDATA%\SoftSplit\config.json`: `sourceName`, `targetName`, `fps`,
   `autostart`, `hotkey`.
3. **UI.** WinForms (проще, тот же стек, что уже используется) или WPF (красивее).
   Элементы: списки дисплеев, слайдер FPS, тумблер, кнопка «Тест», иконка в трее.
4. **Горячая клавиша.** `RegisterHotKey` (Win32) — включение/выключение без мыши.
5. **Упаковка.** `ps2exe` или установщик Inno Setup; либо оставить `.cmd` + ярлык.

Ограничение, которое нельзя терять: **UI не должен требовать прав администратора**,
а `.cmd`-путь обязан остаться рабочим как аварийный.

---

## 7. Стиль кода

- Отступ — 4 пробела; функции — `Verb-Noun` (`Get-`, `Start-`, `Stop-`, `Test-`).
- C#-вставки — в here-string `@' ... '@` (одинарные кавычки: без подстановок PowerShell).
- Комментарии — по-английски, коротко, только там, где неочевидно.
- Никаких `Write-Host` с цветом в библиотечных функциях — только в точках входа.
