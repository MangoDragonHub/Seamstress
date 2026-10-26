Seamstress
==========

Extract the whole folder before starting. Starting the program from inside the .zip file, or replacing files
while it runs, can leave it unable to start.

Windows
  Start Seamstress.exe.
  It needs the Microsoft .NET 8 runtime: https://dotnet.microsoft.com/download/dotnet/8.0

Linux
  Start ./seamstress.sh.

If the window does not appear
  The log ends with the reason:
    Windows  %USERPROFILE%\Documents\Seamstress\logs
    Linux    ~/.config/Seamstress/logs
    Flatpak  ~/.var/app/io.github.mangodragonhub.Seamstress/config/Seamstress/logs
  The 3D viewport needs a working graphics driver. Seamstress.exe --cpu (or ./seamstress.sh --cpu) renders the
  2D interface on the CPU instead.
  If the program says the installation is incomplete, extract the archive again into a new folder.

Licences
  See LICENSES/ and NOTICE. Seamstress is MIT licensed; a package also contains Qt and Qt Quick 3D, which make
  the package as a whole GPL-3.0.
