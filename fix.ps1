param()
$files = Get-ChildItem -Path "c:\Users\yeyil\Documents\GitHub\OmenFlow\OmenFlow.App" -Include *.xaml, *.cs, *.resw -Recurse
foreach ($f in $files) {
    $content = [System.IO.File]::ReadAllText($f.FullName)
    [System.IO.File]::WriteAllText($f.FullName, $content, [System.Text.Encoding]::UTF8)
}
