# Creates the Sac311 database on the local SQL Server Express instance if it doesn't exist.
# Usage: ./scripts/create-db.ps1 [-Server 'localhost\SQLEXPRESS'] [-Database Sac311]
param(
    [string]$Server = 'localhost\SQLEXPRESS',
    [string]$Database = 'Sac311'
)
$ErrorActionPreference = 'Stop'

sqlcmd -S $Server -E -C -b -Q "IF DB_ID(N'$Database') IS NULL CREATE DATABASE [$Database];"
if ($LASTEXITCODE -ne 0) { throw "sqlcmd failed with exit code $LASTEXITCODE" }
Write-Host "Database '$Database' is ready on $Server."
