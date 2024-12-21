try {
	
	
	
	$repo_name = 'LocalSunSharpUtils'
	$dep_path = Resolve-Path '.\0Deployed'
	Write-Host "Initing new $repo_name repo at: $dep_path"
	
	New-Item -Path $dep_path -ItemType Directory -Force
	
	nuget sources Add -Name $repo_name -Source $dep_path
	if (-not $?) {
		try {
			$old_dep_path = (nuget sources list | Select-String "$repo_name" -Context 0,1).Context.PostContext[0].Trim()
		} catch {
			throw "nuget sources Add failed, but existing $repo_name repo was not found"
		}
		
		if ($old_dep_path -ne $dep_path) {
			Write-Host "Old repo found at: $old_dep_path"
			Write-Host "Do you want to re-create it?"
			pause
			nuget sources Remove -Name $repo_name
			if (-not $?) { throw "nuget sources Remove failed" }
			nuget sources Add -Name $repo_name -Source $dep_path
			if (-not $?) { throw "nuget sources Add failed" }
		} else {
			Write-Host "Repo is already set up correctly"
		}
		
	}
	
	
	
}
catch {
	Write-Host "An error occurred:"
	Write-Host $_
	pause
	exit 1
}
pause