'use strict'
// JS shim for the genuine native addon (node v11 ABI — replaced).
const make = require('./generic-addon.js');
module.exports = make({});
