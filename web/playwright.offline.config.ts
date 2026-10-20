import {defineConfig,devices} from '@playwright/test';

const liveURL=process.env['FIRSTBLICK_LIVE_URL'];
if(!liveURL)throw new Error('FIRSTBLICK_LIVE_URL must identify the explicitly started synthetic production application. Offline tests create real records; no existing server is selected implicitly.');
const target=new URL(liveURL);
if(!['http:','https:'].includes(target.protocol)||target.username||target.password||target.search||target.hash||target.pathname!=='/')throw new Error('FIRSTBLICK_LIVE_URL must be an HTTP(S) origin without credentials, path, query or fragment.');

export default defineConfig({
  testDir:'tests/live',testMatch:'offline-workday.spec.ts',workers:1,fullyParallel:false,retries:0,timeout:240000,
  outputDir:'test-results/live-offline',reporter:[['list'],['json',{outputFile:'test-results/live-offline-report.json'}]],
  expect:{timeout:20000},
  use:{baseURL:target.origin,timezoneId:'Europe/Berlin',actionTimeout:30000,navigationTimeout:45000,trace:'retain-on-failure',launchOptions:process.env['FIRSTBLICK_TEST_CHROME']?{executablePath:process.env['FIRSTBLICK_TEST_CHROME']}:{}},
  projects:[{name:'desktop',use:{...devices['Desktop Chrome'],viewport:{width:1440,height:1000}}},{name:'tablet',use:{...devices['Desktop Chrome'],viewport:{width:1024,height:768}}},{name:'mobile',use:{...devices['Desktop Chrome'],viewport:{width:390,height:844},isMobile:true,hasTouch:true}}],
});
