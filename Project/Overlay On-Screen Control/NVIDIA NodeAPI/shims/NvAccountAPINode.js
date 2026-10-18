'use strict'
// JS shim for the genuine native addon (node v11 ABI - replaced).
// Account routes: UserToken / PrivacySettings — หน้าเรียกบ่อยสุดตอนบูต (×14)
const make = require('./generic-addon.js');

const specific = {
  // GET /Account/v.1.0/UserToken — token ปลอมแบบ local (หน้าใช้ต่อ account state)
  UserToken: function (doReply) {
    if (typeof doReply === 'function') doReply(null, { userToken: 'duluka-local-token', userInfo: JSON.stringify({ buildPreference: 'release' }), userId: 1, name: 'Duluka' });
  },
  // GET /Account/v.1.0/PrivacySettings?clientId=...
  GetPrivacySettings: function (doReply) {
    if (typeof doReply === 'function') doReply(null, { privacySettings: { userDataCollection: false } });
  },
  SetPrivacySettings: function (doReply) {
    if (typeof doReply === 'function') doReply(null, {});
  },
};

module.exports = make(specific);
