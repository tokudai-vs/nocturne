export type Encoder = 'h264_nvenc' | 'h264_qsv' | 'h264_amf' | 'libx264';

export interface EncoderResult {
  preferred: Encoder;
  available: Encoder[];
  probedAt: number;
}
