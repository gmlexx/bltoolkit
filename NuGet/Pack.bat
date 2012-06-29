cd ..\Source
call Compile4.bat

cd ..\DataProviders
call Compile4.bat

cd ..\NuGet

del *.nupkg

NuGet Pack BLToolkit.nuspec
rename BLToolkit.4.*.nupkg BLToolkit.nupkg

NuGet Pack BLToolkit.MySql.nuspec
rename BLToolkit.MySql.*.nupkg BLToolkit.MySql.nupkg
