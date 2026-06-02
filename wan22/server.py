"""
Wan 2.2 TI2V-5B inference server.

Endpoints:
  GET  /health  -> {"ok": true, "device": "cuda"}
  POST /infer   -> {"blobUrl": "<sas-url>", "seconds": 5.0, "frames": 120}

Auth: shared bearer token via env WAN_AUTH_TOKEN. Caller must send
      Authorization: Bearer <token>.

Output: uploads MP4 to Azure Blob and returns a read SAS URL valid 24h.
        Set BLOB_ACCOUNT, BLOB_CONTAINER, BLOB_ACCOUNT_KEY env vars.
"""
import os
import uuid
import time
import logging
import datetime as dt
from typing import Optional

import torch
from fastapi import FastAPI, HTTPException, Header
from pydantic import BaseModel, Field
from diffusers import WanPipeline
from diffusers.utils import export_to_video
from azure.storage.blob import BlobServiceClient, BlobSasPermissions, generate_blob_sas

logging.basicConfig(level=logging.INFO, format="%(asctime)s %(levelname)s %(message)s")
log = logging.getLogger("wan22")

WAN_AUTH_TOKEN = os.environ.get("WAN_AUTH_TOKEN", "")
BLOB_ACCOUNT = os.environ.get("BLOB_ACCOUNT", "")
BLOB_CONTAINER = os.environ.get("BLOB_CONTAINER", "videos")
BLOB_KEY = os.environ.get("BLOB_ACCOUNT_KEY", "")
WEIGHTS_DIR = os.environ.get("WEIGHTS_DIR", "/weights/wan22")

log.info("Loading Wan 2.2 TI2V-5B from %s ...", WEIGHTS_DIR)
t0 = time.time()
pipe = WanPipeline.from_pretrained(WEIGHTS_DIR, torch_dtype=torch.bfloat16).to("cuda")
pipe.enable_attention_slicing()
log.info("Model loaded in %.1fs", time.time() - t0)

app = FastAPI(title="wan22-infer", version="1.0")


class InferRequest(BaseModel):
    prompt: str = Field(..., min_length=1, max_length=4000)
    seconds: float = Field(5.0, ge=1.0, le=10.0)
    width: int = Field(1280, ge=320, le=1920)
    height: int = Field(720, ge=320, le=1080)
    fps: int = Field(24, ge=8, le=30)
    steps: int = Field(40, ge=10, le=80)
    seed: Optional[int] = None


def _check_auth(authorization: Optional[str]) -> None:
    if not WAN_AUTH_TOKEN:
        return  # auth disabled
    if not authorization or not authorization.startswith("Bearer "):
        raise HTTPException(status_code=401, detail="missing bearer token")
    if authorization.split(" ", 1)[1] != WAN_AUTH_TOKEN:
        raise HTTPException(status_code=403, detail="invalid token")


def _upload_and_sign(local_path: str) -> str:
    if not BLOB_ACCOUNT or not BLOB_KEY:
        raise HTTPException(status_code=500, detail="BLOB_ACCOUNT/BLOB_ACCOUNT_KEY not configured")
    blob_name = f"wan22/{dt.datetime.utcnow():%Y/%m/%d}/{uuid.uuid4().hex}.mp4"
    svc = BlobServiceClient(
        account_url=f"https://{BLOB_ACCOUNT}.blob.core.windows.net",
        credential=BLOB_KEY,
    )
    client = svc.get_blob_client(BLOB_CONTAINER, blob_name)
    with open(local_path, "rb") as f:
        client.upload_blob(f, overwrite=True, content_type="video/mp4")
    sas = generate_blob_sas(
        account_name=BLOB_ACCOUNT,
        container_name=BLOB_CONTAINER,
        blob_name=blob_name,
        account_key=BLOB_KEY,
        permission=BlobSasPermissions(read=True),
        expiry=dt.datetime.utcnow() + dt.timedelta(hours=24),
    )
    return f"https://{BLOB_ACCOUNT}.blob.core.windows.net/{BLOB_CONTAINER}/{blob_name}?{sas}"


@app.get("/health")
def health():
    return {
        "ok": True,
        "device": str(pipe.device),
        "cuda": torch.cuda.is_available(),
        "vram_gb": round(torch.cuda.get_device_properties(0).total_memory / 1e9, 1) if torch.cuda.is_available() else 0,
    }


@app.post("/infer")
def infer(req: InferRequest, authorization: Optional[str] = Header(default=None)):
    _check_auth(authorization)

    # round dims to multiples of 8 (Wan requires)
    w = (req.width // 8) * 8
    h = (req.height // 8) * 8
    num_frames = int(req.seconds * req.fps)

    log.info("Inferring: %dx%d %d frames (%.1fs @ %dfps) steps=%d", w, h, num_frames, req.seconds, req.fps, req.steps)
    t0 = time.time()
    generator = torch.Generator(device="cuda").manual_seed(req.seed) if req.seed is not None else None

    out = pipe(
        prompt=req.prompt,
        height=h,
        width=w,
        num_frames=num_frames,
        num_inference_steps=req.steps,
        generator=generator,
    ).frames[0]

    elapsed = time.time() - t0
    log.info("Inference done in %.1fs", elapsed)

    local = f"/tmp/{uuid.uuid4().hex}.mp4"
    export_to_video(out, local, fps=req.fps)
    blob_url = _upload_and_sign(local)
    try:
        os.remove(local)
    except OSError:
        pass

    return {
        "blobUrl": blob_url,
        "seconds": num_frames / req.fps,
        "frames": num_frames,
        "width": w,
        "height": h,
        "inferenceSeconds": round(elapsed, 1),
    }
