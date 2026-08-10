package com.github.xandergos.terraindiffusionmc.pipeline;
import java.nio.file.Path;
public final class ModelAssetManager {
    public static void ensureAssetsReady() {}
    public static Path resolveAssetPath(String f) {
        return Path.of(System.getProperty("user.home"), ".config/VintagestoryData/TerrainDiffusionModels", f);
    }
}
