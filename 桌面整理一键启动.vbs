Set W = CreateObject("WScript.Shell")
exe = "D:\WorkBuddy\桌面美化\src\DesktopSuite\bin\x64\Release\self-contained\DesktopSuite.exe"
W.CurrentDirectory = "D:\WorkBuddy\桌面美化\src\DesktopSuite\bin\x64\Release\self-contained"
W.Run """" & exe & """", 1
