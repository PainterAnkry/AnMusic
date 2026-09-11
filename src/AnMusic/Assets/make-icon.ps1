# 生成 AnMusic 应用图标。
#
# 图标来源已改为品牌图 AnMusic.png（仓库根目录），统一由 make-icon.py 处理
# （裁掉透明留白 → 各尺寸 LANCZOS 降采样 → 多尺寸 app.ico + 256 预览图）。
# 这里保留一个入口，避免有人沿用旧脚本时把 app.ico 覆盖回"手绘音符"的旧图标。
#
# 需要 Python 3 + Pillow：pip install pillow
$ErrorActionPreference = 'Stop'

$script = Join-Path $PSScriptRoot 'make-icon.py'
python $script @args
exit $LASTEXITCODE
