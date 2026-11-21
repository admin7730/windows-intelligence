import { spawn } from 'node:child_process';
import { ElevenLabsClient } from '@elevenlabs/elevenlabs-js';

const key = process.env.ELEVENLABS_API_KEY;
const app = process.argv[2];
if (!key || !app) throw new Error('Load the project credential and provide the built WindowsIntelligence.exe path.');
if (process.argv.includes('--verify-model')) {
  const client = new ElevenLabsClient({ apiKey: key });
  const models = await client.models.list();
  if (!models.some(model => model.modelId === 'eleven_flash_v2_5' && model.canDoTextToSpeech)) {
    throw new Error('ElevenLabs did not list the default speech model.');
  }
  console.log('ElevenLabs authentication and text-to-speech model verified.');
}
const result = await new Promise((resolve, reject) => {
  const child = spawn(app, ['--import-elevenlabs-credential'], { stdio: ['pipe', 'ignore', 'ignore'], windowsHide: true });
  child.once('error', reject);
  child.once('exit', resolve);
  child.stdin.once('error', reject);
  child.stdin.end(`${key}\n`);
});
if (result !== 0) throw new Error('Windows Credential Manager import failed.');
console.log('Credential stored in Windows Credential Manager.');
