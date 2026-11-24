param(
  [string]$OutDir = "D:\projects\todo\docs\images"
)
# ASCII-only: Windows PowerShell 5.1 reads BOM-less scripts as ANSI.
[Console]::OutputEncoding = [Text.Encoding]::UTF8
Add-Type -AssemblyName System.Drawing
Add-Type -AssemblyName System.Windows.Forms
Add-Type -Path "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\UIAutomationClient\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationClient.dll"
Add-Type -Path "$env:WINDIR\Microsoft.NET\assembly\GAC_MSIL\UIAutomationTypes\v4.0_4.0.0.0__31bf3856ad364e35\UIAutomationTypes.dll"
Add-Type @"
using System;
using System.Runtime.InteropServices;
public static class S {
  [DllImport("user32.dll")] public static extern bool SetForegroundWindow(IntPtr h);
  [DllImport("user32.dll")] public static extern bool BringWindowToTop(IntPtr h);
  [DllImport("user32.dll")] public static extern bool MoveWindow(IntPtr h,int x,int y,int w,int t,bool r);
  [DllImport("user32.dll")] public static extern bool GetWindowRect(IntPtr h, out RECT r);
  [DllImport("user32.dll")] public static extern bool SetCursorPos(int x,int y);
  [DllImport("user32.dll")] public static extern void mouse_event(uint f,uint x,uint y,uint d,UIntPtr e);
  public struct RECT { public int L,T,R,B; }
}
"@
$A = [System.Windows.Automation.AutomationElement]
$TS = [System.Windows.Automation.TreeScope]
$CC = [System.Windows.Automation.Condition]
$PC = [System.Windows.Automation.PropertyCondition]
$IP = [System.Windows.Automation.InvokePattern]::Pattern

function U([int[]]$cps) { -join ($cps | ForEach-Object { [char]$_ }) }
$N_TODO   = U @(84,111,68,111,111,111)                # ToDooo
$N_EDITOR = U @(24453,21150)                          # editor
$N_CAL    = U @(26085,21382)                          # calendar
$N_NEW    = U @(26032,24314,24453,21150)              # new todo
$N_DETAIL = U @(35814,32454,28155,21152)              # detail add
$N_DUE    = U @(21551,29992,25130,27490,26102,38388)  # enable due
$N_CANCEL = U @(21462,28040)                          # cancel
$N_EDIT   = U @(32534,36753)                          # edit

$proc = Get-Process ToDooo | Sort-Object StartTime -Descending | Select-Object -First 1
if (-not $proc) { throw "no ToDooo process" }
$pidNum = $proc.Id
Write-Host "using pid $pidNum"
New-Item -ItemType Directory -Path $OutDir -Force | Out-Null

function AppWindows {
  $A::RootElement.FindAll($TS::Children, $CC::TrueCondition) |
    Where-Object { $_.Current.ProcessId -eq $pidNum }
}

function Shot([IntPtr]$h, [string]$name) {
  $r = New-Object S+RECT
  [S]::GetWindowRect($h, [ref]$r) | Out-Null
  $w = $r.R - $r.L; $ht = $r.B - $r.T
  if ($w -le 10 -or $ht -le 10) { throw "bad rect for ${name}: ${w}x${ht}" }
  $bmp = New-Object System.Drawing.Bitmap($w, $ht)
  $g = [System.Drawing.Graphics]::FromImage($bmp)
  $g.CopyFromScreen($r.L, $r.T, 0, 0, $bmp.Size)
  $path = Join-Path $OutDir $name
  $bmp.Save($path, [System.Drawing.Imaging.ImageFormat]::Png)
  $g.Dispose(); $bmp.Dispose()
  Write-Host ("  saved {0}  ({1}x{2})" -f $name, $w, $ht)
}

function Place([IntPtr]$h, [int]$x, [int]$y, [int]$w, [int]$ht) {
  [S]::MoveWindow($h, $x, $y, $w, $ht, $true) | Out-Null
  [S]::SetForegroundWindow($h) | Out-Null
  [S]::BringWindowToTop($h) | Out-Null
  Start-Sleep -Milliseconds 1100
}

function ClickAt([int]$x, [int]$y, [int]$delay = 700) {
  [S]::SetCursorPos($x, $y) | Out-Null; Start-Sleep -Milliseconds 350
  [S]::mouse_event(0x0002, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds 80
  [S]::mouse_event(0x0004, 0, 0, 0, [UIntPtr]::Zero); Start-Sleep -Milliseconds $delay
}

# ---------------------------------------------------------------- 1. main window
$todo = AppWindows | Where-Object { $_.Current.Name -eq $N_TODO } | Select-Object -First 1
if (-not $todo) { throw "main window not found" }
$th = [IntPtr]$todo.Current.NativeWindowHandle
Place $th 60 40 440 900
Shot $th "main.png"

# ---------------------------------------------------------------- 2. todo editor (edit an existing item)
# re-resolve the window: the handle captured earlier can go stale after the window is moved
$todo = AppWindows | Where-Object { $_.Current.Name -eq $N_TODO } | Select-Object -First 1
if (-not $todo) { throw "main window disappeared" }
$th = [IntPtr]$todo.Current.NativeWindowHandle
[S]::SetForegroundWindow($th) | Out-Null
[S]::BringWindowToTop($th) | Out-Null
Start-Sleep -Milliseconds 900
$todo = $A::FromHandle($th)

$items = $null
for ($t2 = 0; $t2 -lt 6 -and (-not $items -or $items.Count -eq 0); $t2++) {
  $items = $todo.FindAll($TS::Descendants, $PC::new($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::ListItem))
  if ($items.Count -eq 0) { Start-Sleep -Milliseconds 600 }
}
Write-Host "  todo rows: $($items.Count)"
if ($items.Count -gt 0) {
  # right-click near the right end of the row: away from the subtask strip so the
  # row's own context menu opens, not a subtask's
  $ir = $items[0].Current.BoundingRectangle
  $cx = [int]($ir.Right - 30); $cy = [int]($ir.Top + 16)
  $menu = $null
  for ($attempt = 0; $attempt -lt 3 -and -not $menu; $attempt++) {
    [S]::SetCursorPos($cx, $cy) | Out-Null; Start-Sleep -Milliseconds 350
    [S]::mouse_event(0x0008, 0, 0, 0, [UIntPtr]::Zero)   # right button down
    Start-Sleep -Milliseconds 90
    [S]::mouse_event(0x0010, 0, 0, 0, [UIntPtr]::Zero)   # right button up
    Start-Sleep -Milliseconds 1000

    $menu = $A::RootElement.FindAll($TS::Descendants, $PC::new($A::ControlTypeProperty, [System.Windows.Automation.ControlType]::MenuItem)) |
      Where-Object { $_.Current.Name -like ($N_EDIT + "*") } | Select-Object -First 1
    if (-not $menu) {
      Write-Host "  context menu not open yet, retrying..."
      [S]::SetCursorPos(20, 500) | Out-Null
      Start-Sleep -Milliseconds 400
    }
  }
  if (-not $menu) { throw "context menu edit item not found" }
  $mr = $menu.Current.BoundingRectangle
  ClickAt ([int]($mr.Left + $mr.Width/2)) ([int]($mr.Top + $mr.Height/2)) 2600

  $ed = AppWindows | Where-Object { $_.Current.Name -eq $N_EDITOR } | Select-Object -First 1
  if ($ed) {
    $eh = [IntPtr]$ed.Current.NativeWindowHandle
    Place $eh 200 20 440 820
    Shot $eh "editor.png"

    $ed = $A::FromHandle($eh)
    $cancel = $ed.FindFirst($TS::Descendants, $PC::new($A::NameProperty, $N_CANCEL))
    if ($cancel) { $cancel.GetCurrentPattern($IP).Invoke(); Start-Sleep -Milliseconds 1300 }
  } else {
    Write-Host "editor window not found"
  }
} else {
  Write-Host "no todo items to edit"
}

# ---------------------------------------------------------------- 3. calendar
$todo = AppWindows | Where-Object { $_.Current.Name -eq $N_TODO } | Select-Object -First 1
if (-not $todo) { throw "main window disappeared" }
$th = [IntPtr]$todo.Current.NativeWindowHandle
[S]::SetForegroundWindow($th) | Out-Null
Start-Sleep -Milliseconds 700
$todo = $A::FromHandle($th)
$calBtn = $todo.FindFirst($TS::Descendants, $PC::new($A::NameProperty, $N_CAL))
if ($calBtn) {
  $calBtn.GetCurrentPattern($IP).Invoke()
  Start-Sleep -Milliseconds 4800
  $cal = AppWindows | Where-Object { $_.Current.Name -eq $N_CAL } | Select-Object -First 1
  if ($cal) {
    $ch = [IntPtr]$cal.Current.NativeWindowHandle
    Place $ch 40 40 1180 760
    # the view restores its previous scroll offset; jump to today so the current month is centred
    $cal = $A::FromHandle($ch)
    $today = $cal.FindFirst($TS::Descendants, $PC::new($A::NameProperty, (U @(22238,21040,20170)))) 
    if (-not $today) { $today = $cal.FindFirst($TS::Descendants, $PC::new($A::NameProperty, (U @(22238,21040,20170,22825)))) }
    if ($today) {
      $today.GetCurrentPattern($IP).Invoke()
      Start-Sleep -Milliseconds 2200
    } else {
      Write-Host "  (today button not found, leaving scroll as is)"
    }
    [S]::SetForegroundWindow($ch) | Out-Null
    [S]::BringWindowToTop($ch) | Out-Null
    Start-Sleep -Milliseconds 900
    Shot $ch "calendar.png"
  } else {
    Write-Host "calendar window not found"
  }
} else {
  Write-Host "calendar button not found"
}
Write-Host "done"
