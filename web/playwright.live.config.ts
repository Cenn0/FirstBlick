import { defineConfig,devices } from '@playwright/test';

const liveURL=process.env['FIRSTBLICK_LIVE_URL'];
if(!liveURL)throw new Error('FIRSTBLICK_LIVE_URL must identify the explicitly started synthetic test application. Live tests create real records; an existing development server is never selected implicitly.');
const target=new URL(liveURL);
if(!['http:','https:'].includes(target.protocol)||target.username||target.password||target.search||target.hash||target.pathname!=='/')throw new Error('FIRSTBLICK_LIVE_URL must be an HTTP(S) origin without credentials, path, query or fragment.');

export default defineConfig({
  testDir:'tests/live',testIgnore:'offline-workday.spec.ts',workers:1,fullyParallel:false,retries:0,timeout:240000,
  outputDir:'test-results/live',
  expect:{timeout:20000},
  reporter:[['list'],['json',{outputFile:'test-results/live-report.json'}]],
  use:{baseURL:target.origin,actionTimeout:30000,navigationTimeout:45000,trace:'retain-on-failure',launchOptions:process.env['FIRSTBLICK_TEST_CHROME']?{executablePath:process.env['FIRSTBLICK_TEST_CHROME']}:{}},
  projects:[{name:'desktop',use:{...devices['Desktop Chrome'],viewport:{width:1440,height:1000}}},{name:'tablet',use:{...devices['Desktop Chrome'],viewport:{width:1024,height:768}}},{name:'mobile',use:{...devices['Desktop Chrome'],viewport:{width:390,height:844},isMobile:true,hasTouch:true}}],
  webServer:undefined
});
