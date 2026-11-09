# Face landmark model

`face_landmarks.onnx`: Google MediaPipe Face Mesh V2 landmark network (from `face_landmarker.task`, float16 bundle v1), converted to ONNX (tf2onnx, opset 17) without retraining.

- License: Apache License 2.0 (weights © Google LLC).
- Downloaded 2026-11-08 from https://huggingface.co/naklitechie/face-landmarks-onnx (`face_landmarks.onnx`, 4,920,995 bytes).
- SHA-256: `f38c3321ceffbc9e95103480ad38cc3f52e7e1bde2bcee7cd9355d0b9138ac0c` (matches the value published on the model page).
- Input `input_12`: float32 [1, 256, 256, 3], RGB, 0..1, a square crop around one face.
- Outputs: `Identity` [1, 1, 1, 1434] = 478 landmarks (x, y, z) in crop pixels; `Identity_1` = face-presence logit (sigmoid > 0.5 = face).

Face Studio uses it only to pre-place the front-photo markers in the likeness window; the user can move every marker.
