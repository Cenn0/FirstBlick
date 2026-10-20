import { defineConfig, devices } from '@playwright/test';
export default defineConfig({
  testDir:'tests/browser',
  outputDir:'test-results/browser',
  fullyParallel:true,
  workers:2,
  retries:process.env['CI']?1:0,
  reporter:[['list']],
  use:{baseURL:'http://127.0.0.1:4200',trace:'retain-on-failure',launchOptions:process.env['FIRSTBLICK_TEST_CHROME']?{executablePath:process.env['FIRSTBLICK_TEST_CHROME']}:{}},
  projects:[
    {name:'desktop',use:{...devices['Desktop Chrome'],viewport:{width:1440,height:1000}}},
    {name:'tablet',use:{...devices['Desktop Chrome'],viewport:{width:1024,height:768}}},
    {name:'mobile',use:{...devices['Desktop Chrome'],viewport:{width:390,height:844},isMobile:true,hasTouch:true}}
  ],
  webServer:{command:'npm start',url:'http://127.0.0.1:4200',reuseExistingServer:!process.env['CI'],timeout:120000}
});
