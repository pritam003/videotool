import os, glob, shutil, sys
from huggingface_hub import snapshot_download

os.environ["HF_XET_HIGH_PERFORMANCE"] = "1"
os.makedirs("/models/text_encoders", exist_ok=True)

print("==> downloading umt5 text encoder", flush=True)
snapshot_download(
    repo_id="Kijai/WanVideo_comfy",
    allow_patterns=["*umt5*fp8*.safetensors"],
    local_dir="/tmp/te",
    max_workers=2,
)

print("==> listing /tmp/te:")
for r, _, fs in os.walk("/tmp/te"):
    for f in fs:
        print(" ", os.path.join(r, f))

files = sorted(glob.glob("/tmp/te/**/*umt5*fp8*.safetensors", recursive=True))
if not files:
    print("!! no umt5 fp8 files matched", flush=True)
    sys.exit(2)

src = files[0]
dst = "/models/text_encoders/umt5_xxl_fp8_e4m3fn_scaled.safetensors"
print(f"mv {src} -> {dst}", flush=True)
shutil.move(src, dst)
shutil.rmtree("/tmp/te", ignore_errors=True)

print("==> final /models tree:")
for r, _, fs in os.walk("/models"):
    for f in fs:
        p = os.path.join(r, f)
        print(" ", p, os.path.getsize(p))
print("==> done", flush=True)
