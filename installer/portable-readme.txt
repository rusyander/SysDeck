Windows Process Cleaner - портативная версия
============================================

Что это
  Та же программа, что и в установщике, но без установки: работает из любой папки,
  в том числе с флешки или внешнего диска.

Как запустить
  Запустите WindowsProcessCleaner.exe. Windows один раз спросит разрешение
  администратора - без него нельзя чистить Standby Memory и системные папки.

Где хранятся данные
  Рядом с программой, в папке Data: настройки, история очисток, журналы.
  За это отвечает файл portable.marker - держите его рядом с exe.
  Уберёте метку - программа начнёт писать в %APPDATA%\WindowsProcessCleaner,
  как установленная.

Как удалить
  Удалите эту папку целиком. В системе не остаётся ничего: ни записей в реестре,
  ни файлов в профиле. Если вы включали автозапуск с Windows, снимите его в
  настройках программы до удаления - иначе останется задача планировщика
  "WindowsProcessCleaner", ссылающаяся в пустоту.

Когда лучше установщик
  Если программа нужна на своём компьютере постоянно: он добавит ярлык в меню
  "Пуск" и строку в списке установленных программ, а данные положит в профиль,
  где их не потеряешь вместе с папкой.


Windows Process Cleaner - portable edition
==========================================

What this is
  The same program the installer ships, without installing: it runs from any
  folder, a USB stick or an external drive included.

How to run
  Start WindowsProcessCleaner.exe. Windows asks for administrator rights once -
  without them Standby Memory and system folders cannot be cleaned.

Where data is kept
  Next to the program, in the Data folder: settings, cleanup history, logs.
  The portable.marker file is what makes this happen - keep it next to the exe.
  Remove the marker and the program starts writing to
  %APPDATA%\WindowsProcessCleaner, like an installed copy.

How to remove
  Delete this folder. Nothing is left behind - no registry entries, no files in
  the user profile. If you enabled start with Windows, turn it off in the
  program's settings before deleting, or the scheduled task
  "WindowsProcessCleaner" stays behind pointing at nothing.

When the installer is the better choice
  When you need the program on your own computer permanently: it adds a Start
  menu shortcut and an entry in the installed programs list, and keeps data in
  your profile where it will not disappear along with the folder.
