/* ShadowPlay NEXT — diagnostic-only module loader gate.
 * This script is NOT included in the original index.html.
 * Only runs in generated index.module* and index.core-only pages.
 */
(function(window,document){
  'use strict';
  var current=document.currentScript,raw=current&&current.src.split('?')[1]||'',params={};
  raw.split('&').forEach(function(value){var parts=value.split('=');if(parts[0])params[parts[0]]=decodeURIComponent(parts.slice(1).join('=')||'')});
  var profile=params.profile||'unknown',disabled=(params.omit||'').split(',').filter(Boolean);
  var owners=window.__spnModuleOwners||{},omit=Object.create(null),blocked=[],allowed=[];
  disabled.forEach(function(id){omit[id]=true});
  function getPath(node){
    if(!node||node.nodeType!==1)return null;
    var tag=String(node.tagName||'').toLowerCase();
    if(tag!=='script'&&tag!=='link')return null;
    var value=tag==='script'?(node.getAttribute('src')||node.src):(node.getAttribute('href')||node.href);
    if(!value)return null;
    try{return new URL(value,document.baseURI).pathname}catch(e){return String(value).split('?')[0]}
  }
  function owner(url){
    if(owners[url])return owners[url];
    return null;
  }
  function shouldBlock(node){
    var url=getPath(node);if(!url)return false;
    if(profile==='core-only'&&(/\/mods\//.test(url)||/\/Module\//.test(url)&&!/\/Module\/Core\//.test(url)||url==='/nvcamera-test-mode.js'))return true;
    var id=owner(url);return !!(id&&omit[id]);
  }
  function check(node){
    var url=getPath(node);if(!url)return false;
    if(shouldBlock(node)){blocked.push({url:url,owner:owner(url),time:Date.now()});return true}
    if(/\/mods\/|\/Module\//.test(url))allowed.push({url:url,owner:owner(url),time:Date.now()});
    return false;
  }
  var append=Node.prototype.appendChild,insert=Node.prototype.insertBefore,replace=Node.prototype.replaceChild;
  Node.prototype.appendChild=function(node){if(check(node))return node;return append.call(this,node)};
  Node.prototype.insertBefore=function(node,reference){if(check(node))return node;return insert.call(this,node,reference)};
  Node.prototype.replaceChild=function(node,previous){if(check(node))return previous;return replace.call(this,node,previous)};
  window.__spnModuleAudit={
    profile:profile,omitted:disabled,
    snapshot:function(){return{profile:profile,omitted:disabled.slice(),blocked:blocked.slice(),dynamicAllowed:allowed.slice(),
      loadedScripts:Array.prototype.map.call(document.scripts,function(x){return x.src}).filter(function(url){return /\/mods\/|\/Module\//.test(url)})}}
  };
})(window,document);
