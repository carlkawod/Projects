import greenfoot.*; //importing the Greenfoot library

public class MyWorld extends World {
    private static final int width = 750; //Using constants for the dimensions
    private static final int height = 550;
    private boolean spawn = false; //Flag to prevent re-spawning

    public MyWorld() { 
        super(width, height, 1); 
        showText("Use arrows to move Tux. 'S' to start.", 375, 100); //Instructions
    }
    
    private void prepare() {
        removeObjects(getObjects(null)); //Clearing existing objects to avoid duplication

        addTux();
        addBalloons(75);
        addFish(20);
        addRandomBombs(13);
        
        spawn = false; //Allow for re-preparation if needed
    }

    private void addTux() {
        Tux tux = new Tux();
        addObject(tux, width - 60, height - 60); //Adjusting Tux's starting position if needed (just case)
    }
    
    private void addBalloons(int count) {
        for(int i = 0; i < count; i++) {
            Balloon balloon = new Balloon(); //Adding new balloon
            int x = Greenfoot.getRandomNumber(width);
            int y = Greenfoot.getRandomNumber(height);
            addObject(balloon, x, y);
        }
    }
    
    private void addFish(int count) {
        for(int i = 0; i < count; i++) {
            Fish fish = new Fish(); //adding new fish
            int x = Greenfoot.getRandomNumber(width);
            int y = Greenfoot.getRandomNumber(height);
            addObject(fish, x, y);
        }
    }
    
    private void addRandomBombs(int count) {
        for(int i = 0; i < count; i++) {
            Bomb bomb = new Bomb(); //adding new bomb
            int x = Greenfoot.getRandomNumber(width);
            int y = Greenfoot.getRandomNumber(height);
            //Making sure bombs are not placed too close to Tux's starting position
            if (Math.abs(x - (width - 60)) > 50 || Math.abs(y - (height - 60)) > 50) {
                addObject(bomb, x, y);
            } else {
                i--; // Retrying placement just incase too close
            }
        }
    }

    public void act() {
        if (Greenfoot.isKeyDown("s") && !spawn) { //spawning objest when the letter s is pressed
            spawn = true;
            prepare();
        }
    }
}



