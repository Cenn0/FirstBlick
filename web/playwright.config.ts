import { defineConfig, devices } from '@playwright/test';
const port=process.env['FIRSTBLICK_TEST_PORT']||'4200';
if(!/^\d{4,5}$/.test(port)||Number(port)<1024||Number(port)>65535)throw new Error('FIRSTBLICK_TEST_PORT must be a TCP port from 1024 to 65535.');
const origin=`http://127.0.0.1:${port}`;
export default defineConfig({
  testDir:'tests/browser',
  outputDir:'test-results/browser',
  fullyParallel:true,
  workers:2,
  retries:process.env['CI']?1:0,
  reporter:[['list']],
  use:{baseURL:origin,trace:'retain-on-failure',launchOptions:process.env['FIRSTBLICK_TEST_CHROME']?{executablePath:process.env['FIRSTBLICK_TEST_CHROME']}:{}},
  projects:[
    {name:'desktop',use:{...devices['Desktop Chrome'],viewport:{width:1440,height:1000}}},
    {name:'tablet',use:{...devices['Desktop Chrome'],viewport:{width:1024,height:768}}},
    {name:'mobile',use:{...devices['Desktop Chrome'],viewport:{width:390,height:844},isMobile:true,hasTouch:true}}
  ],
  // Existing servers can contain a different checkout. Reuse is an explicit local opt-in.
  webServer:{command:`npm start -- --port ${port} --live-reload=false --hmr=false`,url:origin,reuseExistingServer:process.env['FIRSTBLICK_REUSE_WEB_SERVER']==='true'&&!process.env['CI'],timeout:120000}
});
