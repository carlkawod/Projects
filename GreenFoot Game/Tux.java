import greenfoot.*;

public class Tux extends Actor {
    private int balloonsPopped = 0;
    private int fishesCaught = 0;

    public void act() {
        checkKeyPress(); //adding key press
        lookOut();
    }

    private void checkKeyPress() {
        int x = getX();
        int y = getY();

        if (Greenfoot.isKeyDown("left")) {//if left key is pressed Tux moves minus one x coordinate (to the left)
            x--;
        }
        if (Greenfoot.isKeyDown("right")) {//if right key is pressed Tux moves plus one x coordinate (to the right)
            x++;
        }
        if (Greenfoot.isKeyDown("up")) {//if up key is pressed Tux moves minus one x coordinate (up)
            y--;
        }
        if (Greenfoot.isKeyDown("down")) {//if left key is pressed Tux moves plus one x coordinate (down)
            y++;
        }

        setLocation(x, y);
    }

    private void lookOut() {
        World myWorld = getWorld(); //Getting reference to the current world

        //Checking for collision with a balloon
        if (isTouching(Balloon.class)) {
            removeTouching(Balloon.class);
            balloonsPopped++;
            Greenfoot.playSound("pop.wav"); //downloaded .wav file from internet
            myWorld.showText("Balloons popped: " + balloonsPopped, 100, 10);
        }

        //Checking for collision with a fish
        if (isTouching(Fish.class)) {
            removeTouching(Fish.class);
            fishesCaught++;
            Greenfoot.playSound("fish.wav"); //downloaded .wav file from internet
            myWorld.showText("Fishes caught: " + fishesCaught, 100, 25);
        }

        //Winning conditions and game over conditions
        int toWinBalloons = 45;
        int toWinFish = 15;
        if (balloonsPopped >= toWinBalloons || fishesCaught >= toWinFish) {
            Greenfoot.playSound("fanfare.wav"); //downloaded .wav file from internet
            myWorld.showText("You won!", myWorld.getWidth() / 2, myWorld.getHeight() / 2);
            Greenfoot.stop();
        }

        // Checking for collision with a bomb
        if (isTouching(Bomb.class)) {
            Greenfoot.playSound("au.wav"); //downloaded .wav file from internet
            myWorld.showText("Game over, so sorry.", myWorld.getWidth() / 2, myWorld.getHeight() / 2);
            Greenfoot.stop();
        }
    }
}

