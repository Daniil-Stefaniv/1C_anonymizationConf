# 1CCommentCleaner

Переносимый очиститель комментариев для XML-выгрузки конфигурации 1С.

## Что внутри

- `Clean1CCommentsGui.exe` - GUI-приложение.
- `clean_1c_comments.py` - консольный вариант очистителя.
- `python\python.exe` - локальный Python, не нужен Python в `PATH`.
- `Reports\` - локальная папка отчетов.
- `Run_GUI.cmd` - запуск GUI.
- `Check_Selected_Folder.cmd` - dry-run без изменений.
- `Check_Selected_Folder_With_XML_Validation.cmd` - dry-run с XML-валидацией.
- `Apply_Selected_Folder_With_Backup.cmd` - применение очистки с backup.
- `Python_SelfTest.cmd` - проверка Python-скрипта.

## Отчеты

Все новые отчеты должны попадать в каталог:

```text
1CCommentCleaner\Reports
```

GUI пишет отчеты туда автоматически. Консольный скрипт также пишет туда отчет по умолчанию, если не передан параметр `--report`.

## Рекомендуемый порядок

1. Запустить `Run_GUI.cmd`.
2. Выбрать папку XML-выгрузки конфигурации 1С.
3. Нажать `Проверить + XML-валидация`.
4. Если ошибок нет, нажать `Очистить + backup`.
5. После очистки загрузить конфигурацию в пустую базу 1С для финальной проверки.

## Консольный запуск

Dry-run:

```bat
Check_Selected_Folder.cmd "D:\Path\To\ConfigDump"
```

Dry-run с XML-валидацией:

```bat
Check_Selected_Folder_With_XML_Validation.cmd "D:\Path\To\ConfigDump"
```

Применить очистку с backup:

```bat
Apply_Selected_Folder_With_Backup.cmd "D:\Path\To\ConfigDump"
```

Все `.cmd` запускают именно локальный:

```text
1CCommentCleaner\python\python.exe
```

и не используют Python из `PATH`.
