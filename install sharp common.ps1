try
{
	$ErrorActionPreference = 'Stop'
	
	
	
	$source_dir = 'C:\0\Prog\Test\Система\sharp common'
	$target_dir = Get-Location
	if ($source_dir -eq $target_dir) {
		throw "Source directory [$source_dir] is the same as target directory [$target_dir]"
	}
	
	$script_name = $MyInvocation.MyCommand.Name
	$script_path = $MyInvocation.MyCommand.Path
	if ($script_path -ne (Join-Path -Path $target_dir -ChildPath $script_name)) {
		throw "Script [$script_name] is not in the target directory [$target_dir]: [$script_path]"
	}
	$renamed_script = Rename-Item -Path $script_path -NewName "_$script_name" -PassThru
	
	$files = Get-ChildItem -Path $source_dir -File
	foreach ($file in $files) {
		Write-Host @"
		cmd /C mklink /H "${target_dir}\$($file.Name)" "$($file.FullName)"
"@
		cmd /C mklink /H "${target_dir}\$($file.Name)" "$($file.FullName)"
		if (-not $?) {
			throw "Failed to create hard link for file [${file.Name}]"
		}
	}
	
	Remove-Item $renamed_script
	
	
	
}
catch {
	Write-Host "An error occurred:"
	Write-Host $_
	pause
	exit 1
}
pause