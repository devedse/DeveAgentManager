import { createRenderer as createSilkieRenderer, stageIcon as silkieStageIcon } from "./silkie-art.js";
import { createRenderer as createSnekRenderer, stageIcon as snekStageIcon } from "./snake-art.js";
import type { PetRenderer, PetFrame } from "./types.js";
import { PointerVelocity, beginThrow, advanceThrow, beginSlide, advanceSlide, type PetFlight, type PetSlide, type Velocity, type ThrowBounds } from "./throw-motion.js";
const SNEK_DEPART_SECONDS = 2.2, SNEK_WALK_SECONDS = 4.6;

// Illustrated character atlases, animated with a mesh rig, eyelids and ink effects.
// Everything is embedded: no network requests, libraries or fonts.
// Pet definitions own their stages, capabilities and renderer. The session below
// never refers to wings, legs, bones or other species-specific anatomy.
function $(id: 'send-off' | 'feed'): HTMLButtonElement;
function $(id: 'buddy-toggle' | 'rig-toggle' | 'motion-toggle' | 'token-amount'): HTMLInputElement;
function $(id: string): HTMLElement;
function $(id: string): HTMLElement { return document.getElementById(id)!; }
const fmt = n => Math.round(n).toLocaleString('en-US');
const clamp = (v,a,b) => Math.max(a,Math.min(b,v));
const ease = v => {v=clamp(v,0,1);return v*v*(3-2*v)};
const icons = {
  idle:'<path d="M5 13q5-7 10 0M7 16h6M10 3v2M3 6l2 2M17 6l-2 2"/>',
  happy:'<path d="M3 10a7 7 0 1 0 14 0 7 7 0 1 0-14 0M6 12q4 5 8 0M7 7h.01M13 7h.01"/>',
  wave:'<path d="m6 11-2-4q0-2 2-1l3 4-1-6q0-2 2-1l2 6 1-5q1-2 2 0l-1 7 2-2q2-1 2 1l-3 6q-2 3-5 2l-4-4q-2-2 0-3Z"/>',
  hop:'<path d="M4 16h12M10 13V3M6 7l4-4 4 4M3 10l1-1M17 10l-1-1"/>',
  walk:'<path d="m8 10 3-4 4 3M8 10l2 3-3 5M8 10l-4 4M10 13l5 4"/><circle cx="12" cy="3" r="1.6"/>',
  peck:'<path d="M3 13h14M5 17h.01M11 16h.01M15 18h.01M6 5q4-5 8 0l-2 5-3-2-2 3"/>',
  sleep:'<path d="M15 3A7 7 0 1 0 17 14 6 6 0 0 1 15 3ZM3 2h4L3 6h4M14 1h3"/>',
  sad:'<circle cx="10" cy="10" r="7"/><path d="M6 14q4-4 8 0M7 7v1M13 7v1"/>',
  surprised:'<circle cx="10" cy="10" r="7"/><ellipse cx="10" cy="13" rx="1.8" ry="2.5"/><path d="M7 7v1M13 7v1M2 2 3 3M18 2l-1 1"/>',
  hatch:'<path d="M5 10 3 8q0-4 7-6 7 2 7 6l-3 2M3 12l4-2 3 3 4-3 3 2q1 6-7 6t-7-6Z"/>',
  slither:'<path d="M2 15c3 0 3-5 6-5s3 5 6 5c2 0 3-2 3-4"/><circle cx="16.5" cy="6.5" r="2.5"/><path d="m15 9 1 2"/>'
};
const icon = id => '<svg viewBox="0 0 20 20" aria-hidden="true">'+(icons[id]||icons.idle)+'</svg>';


// Every pet is a definition: a renderer with setStage(index) and pose(frame),
// stage icons, growth stages, and moves. Motion ids are shared capabilities,
// not joint names; each species brings its own artwork, skeleton and moves.
const PETS={
  silkie:{
    id:'silkie',petName:'Mochi',species:'Silkie',rigLabel:'Silkie · mesh rig',createRenderer:createSilkieRenderer,stageIcon:silkieStageIcon,departSeconds:1.05,
    traits:['A little curious','Very fluffy','Good company'],lines:{grow:'Look at all this new fluff!',feed:'A little work. A little more fluff.'},
    stages:[
      {name:'Speckled egg',short:'Egg',threshold:0,tag:'Someone wonderful is in there.',description:'Silkie chicken · a little possibility',chapter:'the beginning'},
      {name:'Little chick',short:'Chick',threshold:1200,tag:'A whole world to discover.',description:'Silkie chicken · a very small friend',chapter:'the little chick'},
      {name:'Young Silkie',short:'Young',threshold:7000,tag:'Finding those fluffy feet.',description:'Silkie chicken · growing into the fluff',chapter:'finding their feet'},
      {name:'All grown up',short:'Adult',threshold:20000,tag:'Same little heart. So much more fluff.',description:'Speckled Silkie · your fluffy companion',chapter:'full of fluff'}
    ],
    motions:[
      {id:'idle',label:'Idle',duration:Infinity,text:'Just happy to be here.',mood:['Curious & content','Taking it all in, one tiny step at a time.']},
      {id:'happy',label:'Happy',duration:3.3,text:'You make my little day!',mood:['A little delighted','A good day to be a fluffy chicken.']},
      {id:'wave',label:'Wave',duration:3.2,text:'Oh, hello there!',mood:['Always happy to see you','A very enthusiastic little hello.']},
      {id:'hop',label:'Hop',duration:3.15,text:'A tiny jump for joy.',mood:['Full of tiny joy','Those little feet can’t stay still.']},
      {id:'walk',label:'Waddle',duration:4,text:'Places to be. Things to peck.',mood:['Off exploring','A whole meadow of possibilities.']},
      {id:'peck',label:'Peck',duration:3,text:'Is that a little snack?',mood:['Snack investigator','A seed? A crumb? Better check.']},
      {id:'sleep',label:'Sleep',duration:Infinity,text:'Just resting my fluffy eyes…',mood:['Dreaming of little things','A quiet moment in the meadow.']},
      {id:'sad',label:'Sad',duration:3.7,text:'A little cuddle, perhaps?',mood:['Could use a little love','A gentle tap might help.']},
      {id:'surprised',label:'Surprised',duration:2.4,text:'Oh! Was that a seed?',mood:['Oh, goodness!','The world is full of surprises.']},
      {id:'hatch',label:'Wiggle',duration:2,text:'Something good is on the way.',mood:['A little anticipation','Good things take a little time.']},
      {id:'wobble',label:'Lose balance',icon:'surprised',duration:1.2,text:'Whoops, those fluffy feet!',mood:['Finding my feet','A little wobble, then steady again.']},
      {id:'flap',label:'Throw up',icon:'hop',duration:1.4,text:'Flap flap flap!',mood:['Trying to fly','Both wings working very hard.']},
      {id:'toss',label:'Falling',icon:'walk',duration:1.4,text:'A little flying fluff!',mood:['Along for the ride','A small arc, then back to those feet.']},
      {id:'slide',label:'Shove',icon:'walk',duration:2.5,text:'Slippery little feet!',mood:['Sliding on ice','Coasting along until the shove runs out.']},
      {id:'drag',label:'Scared drag',icon:'surprised',duration:2,text:'Whoa, where are we going?',mood:['Hold on tight','A startled face while being dragged.']},
      {id:'land',label:'Landing',icon:'hop',duration:1,text:'Soft landing!',mood:['Feet first','A little squash and a fluff shake.']}
    ]
  },
  snek:{
    id:'snek',petName:'Snek',species:'Snake',rigLabel:'Snek · spine rig',createRenderer:createSnekRenderer,stageIcon:snekStageIcon,departSeconds:SNEK_DEPART_SECONDS,
    traits:['A little cheeky','Very wiggly','Good company'],lines:{grow:'Look how long I’m getting!',feed:'A little work. A little more wiggle.'},
    stages:[
      {name:'Starry egg',short:'Egg',threshold:0,tag:'Something sparkly is in there.',description:'Galaxy snake · a little possibility',chapter:'the beginning'},
      {name:'Little noodle',short:'Noodle',threshold:1200,tag:'Small, but already very curious.',description:'Galaxy snake · a tiny noodle',chapter:'a tiny noodle'},
      {name:'Young Snek',short:'Young',threshold:7000,tag:'Growing one ring at a time.',description:'Galaxy snake · finding its wiggle',chapter:'finding the wiggle'},
      {name:'All grown up',short:'Adult',threshold:20000,tag:'Same little heart. So many more stars.',description:'Galaxy snake · your starry companion',chapter:'full of stars'}
    ],
    motions:[
      {id:'idle',label:'Idle',duration:Infinity,text:'Just happy to be here.',mood:['Curious & content','Watching the meadow, tongue first.']},
      {id:'happy',label:'Happy',duration:3.3,text:'You make my little day!',mood:['A little delighted','Wiggling with joy.']},
      {id:'wave',label:'Wave',duration:3.2,text:'Hello! Rattle rattle!',mood:['Always happy to see you','A very enthusiastic little rattle.']},
      {id:'hop',label:'Hop',duration:3.15,text:'Who says snakes can’t jump?',mood:['Full of tiny joy','Springing about like a coil.']},
      {id:'walk',label:'Slither',icon:'slither',duration:SNEK_WALK_SECONDS,text:'Off for a little slither.',mood:['Off exploring','A whole meadow to slither through.']},
      {id:'peck',label:'Nibble',duration:3,text:'Is that a little snack?',mood:['Snack investigator','A nibble here, a nibble there.']},
      {id:'sleep',label:'Sleep',duration:Infinity,text:'Curled up and cosy…',mood:['Dreaming of starry skies','Curled up in a cosy coil.']},
      {id:'sad',label:'Sad',duration:3.7,text:'A little cuddle, perhaps?',mood:['Could use a little love','A gentle tap might help.']},
      {id:'surprised',label:'Surprised',duration:2.4,text:'Oh! Was that a bug?',mood:['Oh, goodness!','The world is full of surprises.']},
      {id:'hatch',label:'Wiggle',duration:2,text:'Just shaking out the coils.',mood:['A little wiggle','Just shaking out the coils.']},
      {id:'wobble',label:'Lose balance',icon:'surprised',duration:1.2,text:'A wobbly little noodle!',mood:['Finding my balance','The wiggle travels down to the rattle.']},
      {id:'flap',label:'Throw up',icon:'hop',duration:1.4,text:'An airborne noodle!',mood:['Uncoiling in midair','The rattle is still catching up.']},
      {id:'toss',label:'Falling',icon:'slither',duration:1.4,text:'Ssssswoosh!',mood:['A flying S-curve','Head first, with a trailing rainbow tail.']},
      {id:'slide',label:'Shove',icon:'slither',duration:2.5,text:'A slippery noodle!',mood:['Sliding on ice','The coils coast along with a trailing rattle.']},
      {id:'drag',label:'Scared drag',icon:'surprised',duration:2,text:'Ssssomebody hold on!',mood:['Hold on tight','Wide eyes while being dragged.']},
      {id:'land',label:'Coil landing',icon:'sleep',duration:1,text:'Boing! Back in a coil.',mood:['Springy landing','Curl, recoil, then a little rattle.']}
    ]
  }
};

class PetSession{
  definition: typeof PETS.silkie | typeof PETS.snek;
  tokens: number; away: boolean; showBuddy: boolean; showRig: boolean; reduced: boolean; animate: boolean;
  motion: string; motionAt: number; clock: number; previousFrame: number | null;
  gaze: Velocity; targetGaze: Velocity; renderers: PetRenderer[];
  transitioning: boolean; departing: boolean; autoAt: number;
  flight?: PetFlight; slide?: PetSlide; velocity?: Velocity; buddyMotion?: string; buddyMotionAt = 0; dragging = false;

  constructor(definition: typeof PETS.silkie | typeof PETS.snek,save: { tokens?: number; away?: boolean }){
    this.definition=definition;this.tokens=Math.max(0,Math.min(1e9,Number(save.tokens)||20000));
    if(save.tokens===0)this.tokens=0;
    this.away=save.away===true;this.showBuddy=prefs.showBuddy!==false;this.showRig=false;
    this.reduced=matchMedia('(prefers-reduced-motion: reduce)').matches;
    this.animate=!this.reduced;this.motion='idle';this.motionAt=0;this.clock=0;this.previousFrame=null;this.gaze={x:0,y:0};this.targetGaze={x:0,y:0};
    this.renderers=[];this.transitioning=false;this.departing=false;this.autoAt=7;
  }
  get stage(){let s=0;this.definition.stages.forEach((stage,i)=>{if(this.tokens>=stage.threshold)s=i});return s}
  get progress(){const s=this.stage,stage=this.definition.stages[s],next=this.definition.stages[s+1];return next?clamp((this.tokens-stage.threshold)/(next.threshold-stage.threshold),0,1):1}
  addRenderer(mount: HTMLElement,options: { small?: boolean }={}){const renderer=this.definition.createRenderer(mount,options);renderer.setStage(this.stage);this.renderers.push(renderer);return renderer}
  persist(){
    prefs.showBuddy=this.showBuddy;
    try{localStorage.setItem('damagochi-pet-'+this.definition.id,JSON.stringify({tokens:this.tokens,away:this.away}));localStorage.setItem('damagochi-prefs',JSON.stringify(prefs))}catch{}
  }
  action(id,announce=true){
    if(this.away||this.departing)return;
    const action=this.definition.motions.find(a=>a.id===id);if(!action)return;
    this.flight=undefined;this.slide=undefined;this.buddyMotion=undefined;
    this.motion=id;this.motionAt=this.clock;this.autoAt=this.clock+action.duration+6;
    if(id==='flap'||id==='slide') launchBuddy(id==='flap'?{x:260,y:-800}:{x:450,y:120},true);
    const name=this.definition.petName;
    if(announce){say(this.stage===0?'A little wiggle from inside.':action.text);$('pet-status').textContent=this.stage===0?name+' wiggles inside the egg.':name+': '+action.label}
    updateMotionUI();
  }
  draw(now){
    const delta=this.previousFrame===null?0:Math.min(.05,(now-this.previousFrame)/1000);this.previousFrame=now;
    if(this.animate&&!document.hidden)this.clock+=delta;
    if(!this.animate||this.away||!this.showBuddy||this.departing||this.stage===0){this.flight=undefined;this.slide=undefined}
    if(this.slide){
      const stopped=advanceSlide(this.slide,delta,buddyBounds());
      placeBuddy(this.slide.x,this.slide.y);this.velocity={x:this.slide.vx,y:this.slide.vy};
      if(stopped){this.slide=undefined;this.buddyMotion='wobble';this.buddyMotionAt=this.clock}
    }
    if(this.flight){
      const landed=advanceThrow(this.flight,delta,buddyBounds());
      placeBuddy(this.flight.x,this.flight.y);this.velocity={x:this.flight.vx,y:this.flight.vy};
      const motion=landed?'land':this.flight.vy < -80?'flap':'toss';
      if(this.buddyMotion!==motion){this.buddyMotion=motion;this.buddyMotionAt=this.clock}
      if(landed)this.flight=undefined;
    }
    if(this.buddyMotion&&!this.flight&&!this.slide&&!this.dragging&&this.clock-this.buddyMotionAt>(this.buddyMotion==='land'?1:1.2))this.buddyMotion=undefined;
    const follow=this.animate?Math.min(1,delta*7):1;
    this.gaze.x+=(this.targetGaze.x-this.gaze.x)*follow;this.gaze.y+=(this.targetGaze.y-this.gaze.y)*follow;
    const elapsed=this.clock-this.motionAt;
    const action=this.definition.motions.find(a=>a.id===this.motion);
    if(!this.departing&&action&&elapsed>action.duration){this.motion='idle';this.motionAt=this.clock;updateMotionUI()}
    if(this.animate&&!this.away&&!this.departing&&!this.flight&&!this.slide&&!this.dragging&&this.motion==='idle'&&this.clock>this.autoAt){this.autoAt=this.clock+9+Math.random()*5;this.action(this.stage===0?'hatch':(Math.random()>.5?'peck':'hop'),false)}
    const frame: PetFrame={time:this.clock,motion:this.motion,elapsed:this.clock-this.motionAt,gaze:this.gaze,showRig:this.showRig,animate:this.animate,progress:this.progress,transitioning:this.transitioning};
    const placed=this.renderers.map((renderer,index)=>renderer.pose(index===1&&this.buddyMotion?{...frame,motion:this.buddyMotion,elapsed:this.clock-this.buddyMotionAt,velocity:this.velocity}:frame))[0];
    // The shadow follows the main pet across the meadow and shrinks as it leaves the ground.
    // Pets that draw their own shadow (like Snek, whose body lies along the ground) hide it.
    if(placed)$('pet-shadow').style.opacity=placed.ownShadow?'0':'';
    if(placed&&!placed.ownShadow){const units=$('main-pet').clientWidth/480;$('pet-shadow').style.transform='translateX(calc(-50% + '+(placed.rootX*units).toFixed(1)+'px)) scale('+(1-Math.min(.25,placed.lift/180)).toFixed(3)+')'}
  }
}

const readJSON=key=>{try{return JSON.parse(localStorage.getItem(key)||'{}')||{}}catch{return {}}};
const prefs=readJSON('damagochi-prefs');
let session: PetSession = null;
let speechTimer: ReturnType<typeof setTimeout>, toastTimer: ReturnType<typeof setTimeout>, transitionTimer: ReturnType<typeof setTimeout>, departureTimer: ReturnType<typeof setTimeout>;
// Switching pets keeps each pet's own tokens and state; only one is out at a time.
function choosePet(id){
  if(!PETS[id])id='silkie';
  if(session){cancelDrag();if(session.departing)finishDeparture(true);session.persist();session.renderers.forEach(renderer=>renderer.dispose?.())}
  clearTimeout(transitionTimer);clearTimeout(departureTimer);
  const previous=session;
  session=new PetSession(PETS[id],readJSON('damagochi-pet-'+id));
  if(previous){session.showRig=previous.showRig;session.animate=previous.animate;session.targetGaze=previous.targetGaze}
  $('main-pet').replaceChildren();$('buddy-pet').replaceChildren();
  session.addRenderer($('main-pet'));session.addRenderer($('buddy-pet'),{small:true});
  prefs.pet=id;session.persist();
  renderControls();updateUI();
}
function say(text){clearTimeout(speechTimer);$('speech').textContent=text;$('speech').classList.add('visible');speechTimer=setTimeout(()=>$('speech').classList.remove('visible'),2400)}
function toast(text){clearTimeout(toastTimer);$('toast').textContent=text;$('toast').classList.add('visible');toastTimer=setTimeout(()=>$('toast').classList.remove('visible'),2800)}
const PARTICLE_ART={
  heart:'<svg viewBox="0 0 26 24"><path d="M13 21C5 15 2 11 2 7.5 2 4.5 4.5 2 7.5 2 10 2 12 3.5 13 5.5 14 3.5 16 2 18.5 2 21.5 2 24 4.5 24 7.5 24 11 21 15 13 21Z" fill="#f5a3b5" stroke="#1f1a16" stroke-width="2" stroke-linejoin="round"/><path d="M6 7Q7 5 9 5" stroke="#fff" stroke-width="1.8" fill="none" stroke-linecap="round"/></svg>',
  star:'<svg viewBox="0 0 24 24"><path d="M12 1.5Q13.5 10.5 22.5 12 13.5 13.5 12 22.5 10.5 13.5 1.5 12 10.5 10.5 12 1.5Z" fill="#ffd54f" stroke="#1f1a16" stroke-width="1.8" stroke-linejoin="round"/></svg>',
  shell:'<svg viewBox="0 0 30 22"><path d="M2 8 9 2 14 9 20 3 28 10C27 17 21 20 15 20 8 20 3 15 2 8Z" fill="#f8ecd2" stroke="#1f1a16" stroke-width="2" stroke-linejoin="round"/><ellipse cx="17" cy="14" rx="2.6" ry="2" fill="#2b231e"/></svg>'
};
function particles(kind='star',count=7){
  if(!session.animate)return;
  for(let i=0;i<count;i++){
    const p=document.createElement('span');p.className='particle '+kind;p.innerHTML=PARTICLE_ART[kind]||PARTICLE_ART.star;p.style.left=(44+Math.random()*12)+'%';p.style.top=(36+Math.random()*17)+'%';p.style.setProperty('--dx',((Math.random()-.5)*170)+'px');p.style.setProperty('--dy',(-55-Math.random()*85)+'px');p.style.setProperty('--spin',((Math.random()-.5)*70)+'deg');p.style.animationDelay=(i*.06)+'s';$('meadow').append(p);setTimeout(()=>p.remove(),2100);
  }
}
function renderControls(){
  const def=session.definition;
  $('motion-buttons').innerHTML=def.motions.map(m=>'<button class="motion-button'+(m.id===session.motion?' active':'')+'" data-motion="'+m.id+'" aria-pressed="'+(m.id===session.motion)+'">'+icon(m.icon||m.id)+'<span>'+m.label+'</span></button>').join('');
  $('pet-switch').innerHTML=Object.values(PETS).map(d=>'<button class="pet-choice" data-pet="'+d.id+'" aria-pressed="'+(d===def)+'"><span class="pet-face"></span>'+d.petName+'</button>').join('');
  document.querySelectorAll('.pet-choice .pet-face').forEach((slot,i)=>{const d=Object.values(PETS)[i];slot.replaceWith(d.stageIcon(d.stages.length-1))});
  $('pet-name').textContent=def.petName;$('buddy-label').textContent=def.petName+' ♡';$('rig-label').textContent=def.rigLabel;
  $('traits').innerHTML=def.traits.map(t=>'<span class="trait">'+t+'</span>').join('');
  $('stage-picker').innerHTML=session.definition.stages.map((s,i)=>'<button class="stage-pick" data-stage="'+i+'" aria-label="Preview '+s.name+'">'+ '<span class="stage-icon"></span>'+s.short+'</button>').join('');
  document.querySelectorAll('#stage-picker .stage-icon').forEach((slot,i)=>slot.append(session.definition.stageIcon(i)));
  $('brand-mark').replaceChildren(session.definition.stageIcon(session.definition.stages.length-1));
}
function updateMotionUI(){
  const motions=session.definition.motions,info=(motions.find(m=>m.id===session.motion)||motions[0]).mood;
  $('mood-title').textContent=info[0];$('mood-text').textContent=info[1];
  $('activity-badge').textContent=session.away?'Out exploring':session.motion==='sleep'?'Having a little rest':session.motion==='idle'?'Keeping you company':'A little '+(session.definition.motions.find(m=>m.id===session.motion)?.label.toLowerCase()||'adventure');
  document.querySelectorAll<HTMLButtonElement>('[data-motion]').forEach(button=>{const active=button.dataset.motion===session.motion;button.classList.toggle('active',active);button.setAttribute('aria-pressed',String(active));button.disabled=session.away||session.departing||session.transitioning});
}
function updateUI(){
  const s=session.stage,stage=session.definition.stages[s],next=session.definition.stages[s+1];
  const name=session.definition.petName;
  $('main-pet').setAttribute('aria-label','Pet '+name+' ('+stage.name.toLowerCase()+')');$('buddy-pet').setAttribute('aria-label','Pet '+name+', the screen buddy');
  $('token-count').textContent=fmt(session.tokens);$('stage-name').textContent=stage.name;$('stage-tag').textContent=stage.tag;$('pet-description').textContent=stage.description;
  $('stage-stamp').textContent='Chapter 0'+(s+1)+' · '+stage.chapter;
  const progress=next?clamp((session.tokens-stage.threshold)/(next.threshold-stage.threshold)*100,0,100):100;
  $('progress-fill').style.width=progress+'%';$('growth-progress').setAttribute('aria-valuenow',String(Math.round(progress)));
  $('growth-progress').setAttribute('aria-valuetext',next?Math.round(progress)+' percent toward '+next.name:'Fully grown');
  $('progress-label').textContent=next?'Next: '+next.name.toLowerCase():'A lifetime of little adventures';$('progress-count').textContent=next?fmt(Math.max(0,next.threshold-session.tokens))+' to go':'All grown ♡';
  document.querySelectorAll<HTMLButtonElement>('[data-stage]').forEach(button=>{const active=Number(button.dataset.stage)===s;button.classList.toggle('active',active);button.setAttribute('aria-pressed',String(active));button.disabled=session.transitioning||session.departing});
  $('meadow').classList.toggle('away',session.away);$('buddy').hidden=session.away||!session.showBuddy;
  $('send-off').querySelector('span').textContent=session.away?'Invite back':'Send off';$('send-off').disabled=session.departing||session.transitioning;
  $('feed').disabled=session.away||session.transitioning||session.departing;
  $('buddy-toggle').checked=session.showBuddy;$('rig-toggle').checked=session.showRig;$('motion-toggle').checked=session.animate;
  $('rig-label').classList.toggle('visible',session.showRig);document.body.classList.toggle('reduced',!session.animate);
  updateMotionUI();
}
function updateStage(oldStage){
  const newStage=session.stage;
  if(newStage!==oldStage){
    for(const renderer of session.renderers)renderer.setStage(newStage);
    session.motion='idle';session.motionAt=session.clock;session.autoAt=session.clock+6;
    $('pet-status').textContent=session.definition.petName+' is now '+session.definition.stages[newStage].name.toLowerCase()+'.';
  }
  session.persist();updateUI();
}
function addTokens(amount){
  if(session.away||session.transitioning||session.departing)return;
  const oldStage=session.stage;
  const nextTotal=Math.min(1e9,session.tokens+amount);
  if(oldStage===0&&nextTotal>=session.definition.stages[1].threshold&&session.animate){
    session.action('hatch',false);session.transitioning=true;say('A tiny hello is on the way…');updateUI();
    transitionTimer=setTimeout(()=>{session.tokens=nextTotal;session.transitioning=false;updateStage(oldStage);particles('shell',10);session.action('happy',false);toast('Hello, little one. '+session.definition.petName+' has hatched!')},1050);
  }else{
    session.tokens=nextTotal;updateStage(oldStage);session.action(oldStage===0?'hatch':'happy',false);particles();
    const def=session.definition;
    if(session.stage!==oldStage){say(def.lines.grow);toast(def.petName+' grew into '+def.stages[session.stage].name.toLowerCase()+'.')}
    else{say(oldStage===0?'A little closer to hello.':def.lines.feed);toast('+'+fmt(amount)+' demo tokens. A little growing together.')}
  }
}
function pet(){if(session.away||session.departing||session.transitioning)return;session.action(session.stage===0?'hatch':'happy');particles('heart',6)}
choosePet(prefs.pet||'silkie');
$('motion-buttons').addEventListener('click',event=>{const button=(event.target as HTMLElement).closest<HTMLButtonElement>('[data-motion]');if(button)session.action(button.dataset.motion)});
$('stage-picker').addEventListener('click',event=>{
  const button=(event.target as HTMLElement).closest<HTMLButtonElement>('[data-stage]');if(!button||session.transitioning||session.departing)return;
  const stage=Number(button.dataset.stage),old=session.stage;
  session.tokens=session.definition.stages[stage].threshold;updateStage(old);
  if(!session.away)session.action(stage===0?'hatch':'wave',false);
  say('Chapter 0'+(stage+1)+': '+session.definition.stages[stage].name.toLowerCase()+'.');
});
$('feed').addEventListener('click',()=>addTokens(Number($('token-amount').value)));
$('token-amount').addEventListener('change',()=>$('feed-label').textContent='Add '+fmt(Number($('token-amount').value))+' tokens');
$('main-pet').addEventListener('click',pet);
$('main-pet').addEventListener('keydown',event=>{if(event.key===' '||event.key==='Enter'){event.preventDefault();pet()}});
$('buddy-label').addEventListener('click',pet);
$('buddy-pet').addEventListener('keydown',event=>{if(event.key===' '||event.key==='Enter'){event.preventDefault();pet()}});
$('rig-toggle').addEventListener('change',event=>{session.showRig=(event.target as HTMLInputElement).checked;updateUI()});
$('pet-switch').addEventListener('click',event=>{const button=(event.target as HTMLElement).closest<HTMLButtonElement>('[data-pet]');if(button&&button.dataset.pet!==session.definition.id)choosePet(button.dataset.pet)});
$('buddy-toggle').addEventListener('change',event=>{session.showBuddy=(event.target as HTMLInputElement).checked;session.persist();updateUI()});
$('motion-toggle').addEventListener('change',event=>{
  session.animate=(event.target as HTMLInputElement).checked;
  if(!session.animate&&session.departing){clearTimeout(departureTimer);finishDeparture()}
  updateUI();
});
function finishDeparture(quiet=false){session.away=true;session.departing=false;session.motion='idle';session.persist();if(quiet)return;updateUI();toast(session.definition.petName+' is off exploring. Invite them back whenever you like.')}
$('send-off').addEventListener('click',()=>{
  if(session.departing||session.transitioning)return;
  if(session.away){session.away=false;session.persist();updateUI();session.action(session.stage===0?'hatch':'wave');toast('A little friend, right where you left them.');return}
  if(!session.animate||session.stage===0){finishDeparture();return}
  session.departing=true;session.motion='depart';session.motionAt=session.clock;say('See you in a little while!');updateUI();departureTimer=setTimeout(()=>finishDeparture(),session.definition.departSeconds*1000);
});
$('reset').addEventListener('click',()=>{
  clearTimeout(transitionTimer);clearTimeout(departureTimer);session.transitioning=false;session.departing=false;
  const old=session.stage;session.tokens=0;session.away=false;session.motion='idle';session.motionAt=session.clock;
  updateStage(old);toast('A new little story. It starts with an egg.');say('Hello in there.');
});
$('meadow').addEventListener('pointermove',event=>{
  const rect=$('meadow').getBoundingClientRect();session.targetGaze.x=clamp((event.clientX-rect.left-rect.width/2)/(rect.width/2),-1,1);session.targetGaze.y=clamp((event.clientY-rect.top-rect.height*.45)/(rect.height/2),-1,1);
});
$('meadow').addEventListener('pointerleave',()=>{session.targetGaze={x:0,y:0}});
// The screen buddy uses the same release sampling and trajectory as the Portal.
let drag: { id: number; x: number; y: number; left: number; top: number; moved: boolean; velocity: PointerVelocity } | null = null;
function buddyBounds(): ThrowBounds {
  const buddy=$('buddy');
  return {left:0,top:0,right:Math.max(0,innerWidth-buddy.offsetWidth),bottom:Math.max(0,innerHeight-buddy.offsetHeight)};
}
function placeBuddy(x: number,y: number): void {
  const buddy=$('buddy'),bounds=buddyBounds();
  buddy.style.right='auto';buddy.style.bottom='auto';
  buddy.style.left=clamp(x,bounds.left,bounds.right)+'px';buddy.style.top=clamp(y,bounds.top,bounds.bottom)+'px';
}
function launchBuddy(velocity: Velocity,preview=false): void {
  if(!session.animate||session.stage===0||!session.showBuddy)return;
  if(preview)placeBuddy(innerWidth*.45,innerHeight*.5);
  const rect=$('buddy').getBoundingClientRect();
  session.flight=beginThrow(rect.left,rect.top,velocity,$('buddy').offsetWidth,buddyBounds())??undefined;
  session.slide=session.flight?undefined:beginSlide(rect.left,rect.top,velocity)??undefined;
  session.velocity=velocity;session.buddyMotion=session.flight?'flap':session.slide?'slide':'wobble';session.buddyMotionAt=session.clock;
}
function cancelDrag(): void {
  if(drag&&$('buddy-pet').hasPointerCapture(drag.id))$('buddy-pet').releasePointerCapture(drag.id);
  drag=null;session.dragging=false;if(session.buddyMotion==='drag')session.buddyMotion=undefined;$('buddy').classList.remove('dragging');
}
$('buddy-pet').addEventListener('pointerdown',event=>{
  if(event.button!==0||drag||session.away||session.departing)return;
  session.flight=undefined;session.slide=undefined;session.buddyMotion=undefined;
  const rect=$('buddy').getBoundingClientRect();
  drag={id:event.pointerId,x:event.clientX,y:event.clientY,left:rect.left,top:rect.top,moved:false,velocity:new PointerVelocity()};
  drag.velocity.add(rect.left,rect.top,event.timeStamp);$('buddy-pet').setPointerCapture(event.pointerId);
});
$('buddy-pet').addEventListener('pointermove',event=>{
  if(!drag||drag.id!==event.pointerId)return;
  const dx=event.clientX-drag.x,dy=event.clientY-drag.y;
  if(!drag.moved&&Math.hypot(dx,dy)>5){drag.moved=true;session.dragging=true;session.buddyMotion='drag';session.buddyMotionAt=session.clock}
  if(drag.moved){$('buddy').classList.add('dragging');placeBuddy(drag.left+dx,drag.top+dy)}
  const rect=$('buddy').getBoundingClientRect();drag.velocity.add(rect.left,rect.top,event.timeStamp);
});
$('buddy-pet').addEventListener('pointerup',event=>{
  if(!drag||drag.id!==event.pointerId)return;
  const moved=drag.moved,rect=$('buddy').getBoundingClientRect(),velocity=drag.velocity.release(rect.left,rect.top,event.timeStamp);
  cancelDrag();if(moved)launchBuddy(velocity);else pet();
});
$('buddy-pet').addEventListener('pointercancel',cancelDrag);
$('buddy-pet').addEventListener('lostpointercapture',cancelDrag);
$('buddy-pet').addEventListener('keydown',event=>{if(event.key==='Escape'){cancelDrag();session.flight=undefined;session.slide=undefined;session.buddyMotion=undefined}});
addEventListener('resize',()=>{const rect=$('buddy').getBoundingClientRect();placeBuddy(rect.left,rect.top)});
document.addEventListener('visibilitychange',()=>{session.previousFrame=null;if(document.hidden){cancelDrag();session.flight=undefined;session.slide=undefined;session.buddyMotion=undefined}});
requestAnimationFrame(function frame(t){session.draw(t);requestAnimationFrame(frame)});
// Read-only hooks for inspecting the sample in browser developer tools.
(window as any).silkieDemo=(window as any).damagochiDemo={get session(){return session},definitions:PETS,choosePet,version:'5.1'};
