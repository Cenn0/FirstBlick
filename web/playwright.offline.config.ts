import {defineConfig,devices} from '@playwright/test';

export default defineConfig({
  testDir:'tests/live',testMatch:'offline-workday.spec.ts',workers:1,fullyParallel:false,retries:0,timeout:240000,
  outputDir:'test-results/live-offline',reporter:[['list'],['json',{outputFile:'test-results/live-offline-report.json'}]],
  expect:{timeout:20000},
  use:{baseURL:'http://127.0.0.1:4300',timezoneId:'Europe/Berlin',actionTimeout:30000,navigationTimeout:45000,trace:'retain-on-failure',launchOptions:process.env['FIRSTBLICK_TEST_CHROME']?{executablePath:process.env['FIRSTBLICK_TEST_CHROME']}:{}},
  projects:[{name:'desktop',use:{...devices['Desktop Chrome'],viewport:{width:1440,height:1000}}},{name:'tablet',use:{...devices['Desktop Chrome'],viewport:{width:1024,height:768}}},{name:'mobile',use:{...devices['Desktop Chrome'],viewport:{width:390,height:844},isMobile:true,hasTouch:true}}],
  webServer:{command:'npm run preview',url:'http://127.0.0.1:4300',reuseExistingServer:false,timeout:30000}
});
