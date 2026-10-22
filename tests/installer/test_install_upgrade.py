"""Windows integration test: real processes and the compiled Inno shutdown flow.

May display UAC when an existing Kkindle process cannot be inspected.
Uses an isolated directory, no uninstall registration, and no user library.
"""
from pathlib import Path
import os
import subprocess
import tempfile

repo = Path(__file__).resolve().parents[2]
compiler = Path(os.environ['LOCALAPPDATA']) / 'Programs/Inno Setup 6/ISCC.exe'
csc = Path(os.environ['SystemRoot']) / 'Microsoft.NET/Framework64/v4.0.30319/csc.exe'
with tempfile.TemporaryDirectory(prefix='Kkindle-upgrade-') as directory:
    root = Path(directory)
    payload = root / 'payload'
    target = root / 'installed'
    other = root / 'other'
    for folder in (payload / 'Assets', target / 'mcp', other):
        folder.mkdir(parents=True)
    stub = root / 'Stub.cs'
    stub.write_text('class Stub { static void Main(string[] args) { '
                    'if (args.Length > 0) System.IO.File.WriteAllText(args[0], '
                    'new System.Security.Principal.WindowsPrincipal(System.Security.Principal.WindowsIdentity.GetCurrent())'
                    '.IsInRole(System.Security.Principal.WindowsBuiltInRole.Administrator).ToString()); '
                    'System.Threading.Thread.Sleep(120000); } }')
    subprocess.run([str(csc), '/nologo', '/target:winexe', f'/out:{target / "Kkindle.exe"}', str(stub)], check=True)
    import shutil
    for path in (target / 'mcp/Kkindle.McpServer.exe', other / 'Kkindle.exe', payload / 'Kkindle.exe'):
        shutil.copy2(target / 'Kkindle.exe', path)
    shutil.copy2(repo / 'LICENSE', payload / 'LICENSE')
    shutil.copy2(repo / 'src/Kkindle.App/Assets/Icons/Kkindle.ico', payload / 'Assets/Kkindle.ico')
    shutil.copy2(repo / 'installer/ChineseSimplified.isl', root / 'ChineseSimplified.isl')
    source = (repo / 'installer/Kkindle.iss').read_text(encoding='utf-8-sig')
    source = source.replace('PrivilegesRequired=lowest', 'PrivilegesRequired=lowest\nUninstallable=no\nCreateUninstallRegKey=no\nUsePreviousAppDir=no')
    # Omit shortcuts and post-install launches, preserving the production shutdown code.
    start = source.index('[Icons]')
    end = source.index('[UninstallRun]')
    source = source[:start] + source[end:]
    script = root / 'test.iss'
    script.write_text(source, encoding='utf-8-sig')
    subprocess.run([str(compiler), '/Qp', f'/DSourceDir={payload}', f'/DOutputDir={root}', str(script)], check=True)
    sentinel = target / 'data/keep.txt'
    sentinel.parent.mkdir()
    sentinel.write_text('preserve library')
    powershell = Path(os.environ['SystemRoot']) / 'System32/WindowsPowerShell/v1.0/powershell.exe'
    mcp_path = str(target / 'mcp/Kkindle.McpServer.exe').replace("'", "''")
    elevated_marker = root / 'elevated.txt'
    elevated_marker.write_text('waiting')
    marker_argument = ('"' + str(elevated_marker) + '"').replace("'", "''")
    # Run MCP elevated: ordinary Setup must request elevation to stop it.
    processes = [subprocess.Popen([str(target / 'Kkindle.exe')]),
                 subprocess.Popen([str(powershell), '-NoProfile', '-WindowStyle', 'Hidden',
                                   '-Command', f"Start-Process -FilePath '{mcp_path}' -ArgumentList '{marker_argument}' -Verb RunAs -WindowStyle Hidden -Wait"]),
                 subprocess.Popen([str(other / 'Kkindle.exe')])]
    try:
        import time
        for _ in range(100):
            try:
                if elevated_marker.read_text() == 'True':
                    break
            except (FileNotFoundError, PermissionError):
                pass
            time.sleep(0.1)
        assert elevated_marker.read_text() == 'True', 'MCP fixture was not elevated'
        log = root / 'install.log'
        result = subprocess.run([str(root / 'Kkindle-1.0.6-win-x64-setup.exe'), '/VERYSILENT', '/SUPPRESSMSGBOXES', '/NORESTART', f'/DIR={target}', f'/LOG={log}'], timeout=180)
        report = log.read_text(encoding='utf-8-sig')
        evidence = repo / 'artifacts/installer-fix-validation/integration.log'
        evidence.parent.mkdir(parents=True, exist_ok=True)
        evidence.write_text(report, encoding='utf-8')
        assert result.returncode == 0, (result.returncode, report[-3000:])
        for process in processes[:2]:
            process.wait(timeout=10)
        assert processes[2].poll() is None, 'Unrelated process was stopped'
        assert sentinel.read_text() == 'preserve library'
        assert 'Installation process succeeded.' in report
        print('PASS: real installer stopped app + MCP, preserved unrelated process and data')
        print('Elevation retry exercised:', 'retrying the path-scoped helper' in report)
    finally:
        for process in processes:
            if process.poll() is None:
                process.terminate()
            process.wait(timeout=10)
