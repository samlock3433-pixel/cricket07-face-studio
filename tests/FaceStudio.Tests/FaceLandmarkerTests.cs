using Cricket07.FaceStudio.Core;
using Xunit;

namespace Cricket07.FaceStudio.Tests;

public sealed class FaceLandmarkerTests
{
    private static string? Model()
    {
        for (string? dir = AppContext.BaseDirectory; dir is not null; dir = Path.GetDirectoryName(dir))
            if (File.Exists(Path.Combine(dir, "vendor", "facemesh", "face_landmarks.onnx"))) return Path.Combine(dir, "vendor", "facemesh", "face_landmarks.onnx");
        return null;
    }

    [SkippableFact]
    public void ModelLoadsFindsNoFaceInABlankImageAndRejectsATamperedFile()
    {
        string? model = Model();
        Skip.If(model is null, "Face landmark model not present.");
        using (var landmarker = new FaceLandmarker(model!))
            Assert.Null(landmarker.Detect(new byte[320 * 240 * 4], 320, 240));
        string copy = Path.Combine(Path.GetTempPath(), "tampered-" + Guid.NewGuid().ToString("N") + ".onnx");
        try
        {
            var bytes = File.ReadAllBytes(model!); bytes[^1] ^= 1; File.WriteAllBytes(copy, bytes);
            Assert.Throws<InvalidDataException>(() => new FaceLandmarker(copy));
        }
        finally { File.Delete(copy); }
        Assert.Equal(FaceFit.Landmarks.Length, FaceLandmarker.MeshIndex.Length);
        for (int i = 0; i < FaceFit.Landmarks.Length; i++)
            Assert.Equal(FaceFit.Landmarks[i].Front, FaceLandmarker.MeshIndex[i] >= 0); // every front landmark has a mesh point
    }
}
