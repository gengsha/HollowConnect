param([string]$OutputRoot)
$ErrorActionPreference='Stop'
if(!$OutputRoot){throw 'Provide an isolated output directory'}
New-Item -ItemType Directory -Path $OutputRoot -Force | Out-Null
$taskRoot=Split-Path -Parent $PSScriptRoot
Add-Type -AssemblyName System.Windows.Forms,System.Drawing
$taskAssembly=[Reflection.Assembly]::LoadFrom((Join-Path $taskRoot 'src/HollowConnect.Installer/bin/Release/net472/一键安装.exe'))
$taskFormType=$taskAssembly.GetType('HollowConnect.Installer.InstallerForm')
$taskFlags=[Reflection.BindingFlags]'NonPublic,Instance'
$taskForm=[Activator]::CreateInstance($taskFormType,$true)
try{
    # Showing renders child controls; automatic discovery is read-only. Never
    # submit either install/uninstall action or use a real game as fixture input.
    $taskForm.Show();$taskForm.PerformLayout();[Windows.Forms.Application]::DoEvents()
    $taskLanguage=$taskFormType.GetField('language',$taskFlags).GetValue($taskForm)
    $taskStatus=$taskFormType.GetField('status',$taskFlags).GetValue($taskForm)
    $taskFormType.GetMethod('SetStatus',$taskFlags).Invoke($taskForm,@('安装完成！可以启动游戏；所有队友请使用同一模组版本。')) | Out-Null
    foreach($taskIndex in 0,1,2){
        $taskLanguage.SelectedIndex=$taskIndex
        $taskForm.PerformLayout();[Windows.Forms.Application]::DoEvents()
        $taskCode=@('ZH','EN','JA')[$taskIndex]
        if($taskIndex -eq 1 -and $taskStatus.Text -match '[\u4e00-\u9fff]'){throw 'English status was not translated'}
        $taskBitmap=New-Object Drawing.Bitmap($taskForm.Width,$taskForm.Height)
        try{$taskForm.DrawToBitmap($taskBitmap,(New-Object Drawing.Rectangle(0,0,$taskForm.Width,$taskForm.Height)));$taskBitmap.Save((Join-Path $OutputRoot ($taskCode+'.png')))}finally{$taskBitmap.Dispose()}
        Write-Output "PASS installer language selector and retained status: $taskCode"
    }
}finally{$taskForm.Dispose()}
