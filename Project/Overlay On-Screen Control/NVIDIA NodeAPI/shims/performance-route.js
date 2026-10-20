'use strict';
const {execFile} = require('child_process');
const path = require('path');
const os = require('os');
const fs = require('fs');

module.exports = function mountPerformance(app, options) {
  const run = options && options.execFile || execFile;
  let pending, cached, cachedAt = 0;
  function gpus() {
    if (cached && Date.now() - cachedAt < 10000) return Promise.resolve(cached);
    if (pending) return pending;
    pending = new Promise((resolve, reject) => {
      const windows = process.env.SystemRoot || 'C:\\Windows';
      const nativeTool = path.join(windows,'Sysnative','nvidia-smi.exe');
      run(fs.existsSync(nativeTool) ? nativeTool : path.join(windows, 'System32', 'nvidia-smi.exe'),
        ['--query-gpu=index,name,uuid,pci.bus_id', '--format=csv,noheader,nounits'],
        {windowsHide:true, timeout:4000, maxBuffer:65536}, (error, stdout) => {
          if (error) return reject(error);
          let result;
          try { result = String(stdout).trim().split(/\r?\n/).filter(Boolean).map((line, index) => {
            const columns = line.split(',').map(value=>value.trim());
            if (columns.length < 4 || !/^\d+$/.test(columns[0])) throw new Error('Invalid GPU catalog');
            return {index, physicalIndex:Number(columns[0]), name:columns.slice(1,-2).join(', '),
              uuid:columns[columns.length-2], pciBusId:columns[columns.length-1]};
          }); } catch (parseError) { return reject(parseError); }
          if (!result.length) return reject(new Error('No NVIDIA GPU found'));
          resolve(result);
        });
    }).then(result=>{cached=result;cachedAt=Date.now();return result;}).finally(()=>{pending=null;});
    return pending;
  }
  app.post('/Duluka/v.1.0/Performance/Query', async (req,res) => {
    try {
      const body = req.body || await new Promise((resolve,reject)=>{
        let raw='';
        req.on('error',reject);
        req.on('data',chunk=>{raw+=chunk;if(Buffer.byteLength(raw)>16384)reject(new Error('Request too large'));});
        req.on('end',()=>{try{resolve(JSON.parse(raw || '{}'));}catch(error){reject(error);}});
      });
      const command = body.command;
      let payload;
      if (command === 'GetGpuInfo') payload = {result:true,_return_code:0,gpus:await gpus()};
      else if (command === 'GetOCConfig') payload = {result:true,_return_code:0,ocConfig:{
        manualOcSupported:false,manualOcScannerSupported:false,backgroundOcScannerSupported:false,grdOcSupported:false}};
      else if (command === 'GetDefaultLoggingPath') payload = {result:true,_return_code:0,path:{logpath:path.join(os.homedir(),'Videos')}};
      else payload = {result:false,_return_code:-1,code:'PERFORMANCE_OPERATION_UNAVAILABLE',command};
      res.json({payload});
    } catch (error) {
      res.status(503).json({payload:{result:false,_return_code:-1,code:'GPU_CATALOG_UNAVAILABLE',message:error.message}});
    }
  });
};
