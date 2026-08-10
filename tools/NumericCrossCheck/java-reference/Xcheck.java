import com.github.xandergos.terraindiffusionmc.pipeline.*;
import java.util.Locale;

public class Xcheck {
    static StringBuilder sb = new StringBuilder();
    static void emit(String tag, float... vals) {
        sb.append(tag);
        for (float v : vals) sb.append(' ').append(String.format(Locale.ROOT, "%.9g", v));
        sb.append('\n');
    }
    public static void main(String[] a) {
        // 1. PortableRng
        float[] n = new float[16];
        PortableRng.fillStandardNormal(1234567890123L, n, 0, 16);
        emit("rng", n);
        long[] r = PortableRng.pcg64Next(0xDEADBEEFCAFEL);
        emit("pcg", (float) r[1]);
        emit("tileseed", (float) (PortableRng.tileSeed(987654321L, -3, 7) >>> 40));

        // 2. GaussianNoisePatch (negative origin crossing tile boundaries)
        float[][][] p = GaussianNoisePatch.generate(42L, -70, -130, 8, 8, 2, 64, 64);
        float[] flat = new float[2*8*8];
        int k = 0;
        for (int c = 0; c < 2; c++) for (int i = 0; i < 8; i++) for (int j = 0; j < 8; j++) flat[k++] = p[c][i][j];
        emit("noise", flat);

        // 3. EDMScheduler
        EDMScheduler s = new EDMScheduler(20);
        emit("sigmas", s.sigmas);
        float[] sample = new float[32];
        for (int i = 0; i < 32; i++) sample[i] = (float) Math.sin(i * 0.7);
        for (int step = 0; step < 20; step++) {
            float[] mo = new float[32];
            for (int i = 0; i < 32; i++) mo[i] = (float) Math.cos(i * 0.3 + step);
            sample = s.step(mo, sample);
        }
        emit("edm", sample);

        // 4. LaplacianUtils
        int H = 24, W = 20, lh = 6, lw = 5;
        float[][] res = new float[H][W];
        for (int i = 0; i < H; i++) for (int j = 0; j < W; j++) res[i][j] = (float) Math.sin(i * 0.3 + j * 0.17);
        float[][] low = new float[lh][lw];
        for (int i = 0; i < lh; i++) for (int j = 0; j < lw; j++) low[i][j] = (float) Math.cos(i * 0.5 - j * 0.25) * 30f;
        float[][] nl = LaplacianUtils.laplacianDenoise(res, low, 5.0f);
        float[] nlf = new float[lh*lw]; k=0;
        for (int i=0;i<lh;i++) for (int j=0;j<lw;j++) nlf[k++]=nl[i][j];
        emit("denoise", nlf);
        float[][] dec = LaplacianUtils.laplacianDecode(res, nl);
        float[] decf = new float[H]; for (int i=0;i<H;i++) decf[i]=dec[i][i%W];
        emit("decode", decf);
        float[][][] lbt = LaplacianUtils.localBaselineTemperature(res, low2(H,W), 15, 0.02f);
        float[] lbtf = new float[2*4];
        for (int c=0;c<2;c++) for (int i=0;i<4;i++) lbtf[c*4+i]=lbt[c][i][i];
        emit("lbt", lbtf);

        // 5. SyntheticMapFactory
        SyntheticMapFactory f = new SyntheticMapFactory(123456789L);
        float[][][] syn = f.sample(-40, 17, -32, 25);
        float[] synf = new float[5*8*8]; k=0;
        for (int c=0;c<5;c++) for (int i=0;i<8;i++) for (int j=0;j<8;j++) synf[k++]=syn[c][i][j];
        emit("synth", synf);

        System.out.print(sb);
    }
    static float[][] low2(int H, int W) {
        float[][] e = new float[H][W];
        for (int i=0;i<H;i++) for (int j=0;j<W;j++) e[i][j] = (float)((i*W+j) % 7 - 2) * 300f;
        return e;
    }
}
