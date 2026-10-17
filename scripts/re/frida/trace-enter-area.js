// Paste after common.js. Milestones of entering an area plus the usual UI prerequisites: inventory registrations
// (0x141d625f0), inventory lookups that return null (0x141d621a0), passive slot creation (0x142155bf0).
let inventories = 0;
Interceptor.attach(at('0x141d625f0'), { onEnter() { inventories++; } });
Interceptor.attach(at('0x142155bf0'), { onEnter(a) { send({passiveSlot: a[1].toInt32()}); } });
for (const [a, name] of [['0x142186fa0','SetLocalPlayerId'],['0x142156d80','EnterArea'],['0x142183670','WorldActivate'],
                         ['0x141ebe190','CreateObject'],['0x140785160','UiBuild785160']]) {
  Interceptor.attach(at(a), { onEnter() { send({enter: name, inventories}); }, onLeave() { send({leave: name}); } });
}
Interceptor.attach(at('0x141d621a0'), {
  onEnter(a) { this.out = a[1]; this.row = a[2].toUInt32(); this.ret = this.returnAddress; },
  onLeave() { try { if (this.out.readPointer().isNull()) send({missingInventoryId: this.row, from: va(this.ret)}); } catch (e) {} }
});
send({ready: 'trace-enter-area'});
