// Makes a clean overlay config (the one in the repo and in the friend zip) from Kaalob's live one:
// same look (groups, colors, ring), only the sounds on screen. No captions, no voice, no mic changes,
// no hotkeys the game uses, no personal paths or devices.
// node friend-config.js <source config.json> <target config.json>
const fs = require('fs');
const [src, dst] = process.argv.slice(2);
const cfg = JSON.parse(fs.readFileSync(src, 'utf8').replace(/^﻿/, ''));

Object.assign(cfg, {
  GameFeedPath: '../soundfeed.txt',                // next to the overlay folder, where the reader writes it (relative to OtherSideSound.exe)
  GameProcess: 'Phasmophobia',
  Profile: 'phasmophobia',
  SoundNames: [['^Ambient_', '', '']],             // always-on beds, truck, menus, the house's random noise spots
  Device: 'auto',
  WhisperCaptions: false,
  OwnVoiceCaptions: false,
  Speak: false,
  UseKokoro: false,
  SetDefaultMic: false,
  HelperEnabled: false,
  SummaryEnabled: false,
  MouseWheelMenu: false,
  WheelMiddleClick: false,
  SayHomeKey: false,
  SayF6: false,
  SayMouseButton: 0,
  HuntKey: false,
  IntroLine: '',
  IntroLine2: '',
  WhisperScript: '',
  KokoroPython: '', KokoroScript: '', EdgeTtsPython: '', WhisperPython: '',
  MicDevice: '', SpeakDevice: '', OwnVoiceDevice: '',
});

fs.writeFileSync(dst, JSON.stringify(cfg, null, 2));
console.log('config written: ' + dst);
