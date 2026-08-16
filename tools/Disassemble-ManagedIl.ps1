param(
    [Parameter(Mandatory = $true)] [string] $AssemblyPath,
    [Parameter(Mandatory = $true)] [string] $TypeName,
    [Parameter(Mandatory = $true)] [string] $MethodName
)

$opCodes = @{}
[Reflection.Emit.OpCodes].GetFields([Reflection.BindingFlags]'Public,Static') | ForEach-Object {
    $opCode = $_.GetValue($null)
    $opCodes[[int]($opCode.Value -band 0xFFFF)] = $opCode
}

function Read-Int16([byte[]] $bytes, [ref] $position) {
    $value = [BitConverter]::ToInt16($bytes, $position.Value)
    $position.Value += 2
    return $value
}

function Read-Int32([byte[]] $bytes, [ref] $position) {
    $value = [BitConverter]::ToInt32($bytes, $position.Value)
    $position.Value += 4
    return $value
}

function Read-Int64([byte[]] $bytes, [ref] $position) {
    $value = [BitConverter]::ToInt64($bytes, $position.Value)
    $position.Value += 8
    return $value
}

function Resolve-Token($module, [int] $token, [string] $operandType) {
    try {
        switch ($operandType) {
            'InlineString' { return '"' + $module.ResolveString($token) + '"' }
            'InlineSig' { return 'signature 0x{0:X8}' -f $token }
            default {
                $member = $module.ResolveMember($token)
                return $member.DeclaringType.FullName + '::' + $member.ToString()
            }
        }
    }
    catch {
        return 'token 0x{0:X8}' -f $token
    }
}

$assembly = [Reflection.Assembly]::LoadFrom((Resolve-Path -LiteralPath $AssemblyPath))
$type = $assembly.GetType($TypeName, $true)
$methods = $type.GetMethods([Reflection.BindingFlags]'Public,NonPublic,Instance,Static,DeclaredOnly') |
    Where-Object Name -EQ $MethodName

if ($methods.Count -ne 1) {
    throw "Expected exactly one $TypeName.$MethodName method; found $($methods.Count)."
}

$method = $methods[0]
$body = $method.GetMethodBody()
if ($null -eq $body) {
    throw "$TypeName.$MethodName has no managed method body."
}

$bytes = $body.GetILAsByteArray()
$position = 0
"METHOD $($method.DeclaringType.FullName).$($method.Name)"

while ($position -lt $bytes.Length) {
    $offset = $position
    $first = $bytes[$position]
    $position++
    if ($first -eq 0xFE) {
        $key = 0xFE00 -bor $bytes[$position]
        $position++
    }
    else {
        $key = $first
    }

    $opCode = $opCodes[[int] $key]
    if ($null -eq $opCode) {
        throw 'Unknown opcode 0x{0:X4} at IL_{1:X4}.' -f $key, $offset
    }

    $operand = $null
    switch ($opCode.OperandType.ToString()) {
        'InlineNone' { }
        'ShortInlineI' {
            if ($opCode.Name -eq 'ldc.i4.s') {
                $operand = if ($bytes[$position] -gt 127) { [int] $bytes[$position] - 256 } else { $bytes[$position] }
            }
            else {
                $operand = $bytes[$position]
            }
            $position++
        }
        'InlineI' { $operand = Read-Int32 $bytes ([ref] $position) }
        'InlineI8' { $operand = Read-Int64 $bytes ([ref] $position) }
        'ShortInlineR' {
            $operand = [BitConverter]::ToSingle($bytes, $position)
            $position += 4
        }
        'InlineR' {
            $operand = [BitConverter]::ToDouble($bytes, $position)
            $position += 8
        }
        'ShortInlineBrTarget' {
            $delta = if ($bytes[$position] -gt 127) { [int] $bytes[$position] - 256 } else { $bytes[$position] }
            $position++
            $operand = 'IL_{0:X4}' -f ($position + $delta)
        }
        'InlineBrTarget' {
            $delta = Read-Int32 $bytes ([ref] $position)
            $operand = 'IL_{0:X4}' -f ($position + $delta)
        }
        'InlineSwitch' {
            $count = Read-Int32 $bytes ([ref] $position)
            $base = $position + (4 * $count)
            $targets = for ($index = 0; $index -lt $count; $index++) {
                $delta = Read-Int32 $bytes ([ref] $position)
                'IL_{0:X4}' -f ($base + $delta)
            }
            $operand = $targets -join ', '
        }
        'ShortInlineVar' {
            $operand = $bytes[$position]
            $position++
        }
        'InlineVar' {
            $operand = [BitConverter]::ToUInt16($bytes, $position)
            $position += 2
        }
        { $_ -in @('InlineField', 'InlineMethod', 'InlineSig', 'InlineString', 'InlineTok', 'InlineType') } {
            $token = Read-Int32 $bytes ([ref] $position)
            $operand = Resolve-Token $method.Module $token $opCode.OperandType.ToString()
        }
        default { throw "Unsupported operand type $($opCode.OperandType)." }
    }

    if ($null -eq $operand) {
        'IL_{0:X4}: {1}' -f $offset, $opCode.Name
    }
    else {
        'IL_{0:X4}: {1,-12} {2}' -f $offset, $opCode.Name, $operand
    }
}
